# Implementation Plan: Sensor Ingestion, Stateful Rule Evaluation & Alerting

**Branch**: `001-sensor-ingestion-alerting` | **Date**: 2026-10-01 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-sensor-ingestion-alerting/spec.md`

## Summary

SensorGuard reads a JSONL file of sensor readings at startup, rejects and counts invalid lines,
removes duplicates (lowest value wins for conflicting copies, order-independent), orders each (device, metric) series by event time, classifies each
reading against data-driven rules (six stateless operators plus the stateful SustainedAbove),
raises one cooldown-deduplicated alert per sustained episode, persists everything idempotently in
SQLite under natural keys, prints a reconciled processing report, and then serves
`GET /api/aggregations` over acceptable readings. Approach: Clean Architecture on .NET 8 with a pure
Domain, a batch sort-then-scan evaluator (deterministic by construction), pluggable operators
resolved from a registry, hand-written SQL with `ON CONFLICT`, and test-first development with
infrastructure-free domain tests.

## Technical Context

**Language/Version**: C# on .NET 10 (the constitution allows .NET 8 or 10; only the .NET 10 SDK and runtime are installed on the dev machine); nullable enabled, warnings as errors, file-scoped namespaces

**Primary Dependencies**: ASP.NET Core Minimal API; Microsoft.Data.Sqlite; System.Text.Json (Infrastructure and Api only); Microsoft.Extensions.Logging/Options abstractions. Domain: none.

**Storage**: SQLite file via Microsoft.Data.Sqlite, hand-written SQL, no ORM, no generic repository; unique keys + `INSERT ... ON CONFLICT`

**Testing**: xUnit, Shouldly, `WebApplicationFactory`, `FakeTimeProvider`, temp-file fixtures, in-memory SQLite for store tests

**Target Platform**: Local console + HTTP service on Windows/Linux (single process, no deployment)

**Project Type**: web-service (batch ingestion at startup, then read-only API)

**Performance Goals**: supplied file (~2,150 lines) processed in seconds; ingestion O(n), evaluation O(n log n), aggregation one indexed query + O(m)

**Constraints**: two-working-day budget; no brokers, auth, UI, ORM, MediatR, AutoMapper, generic repository, rule CRUD; `TimeProvider` injected, no wall-clock in Domain/Application logic

**Scale/Scope**: ~2,150 readings, a handful of devices and 3 metrics, ~7 rules; memory O(n) for the file's readings (acceptable, trade-off documented)

## Constitution Check

*GATE: passed before Phase 0; re-checked after Phase 1 design (below).*

| # | Principle | Status | How the plan satisfies it / evidence |
|---|-----------|--------|--------------------------------------|
| I | Domain-first, infrastructure-independent (NON-NEGOTIABLE) | PASS | Four src projects, inward references only. Domain has no package references; JSON handled by the neutral `RawReading`/`Field` model (research R1); logging only in Application; SQL only in Infrastructure behind ports. Architecture test fails on Domain → Infrastructure/Sqlite/ASP.NET/System.Text.Json references. Out-of-scope list respected (see Constraints). |
| II | Messy data is the core problem | PASS | `ReadingValidator` with seven reason codes and fixed precedence (R2); rejections never throw, are counted per reason and logged Warning; invalid/duplicate never reach evaluation; dedup policy (lowest value for in-file conflicts, stored value across runs) documented, research R16. |
| III | Deterministic, event-time stateful evaluation | PASS | Batch sort-then-scan (R8); series ordered by `(ts, seq)`; no clock in evaluation; shuffled-input tests (fixtures INCLUDE conflicting duplicates, which the lowest-value rule makes order-independent, R16); strict `>` and inclusive duration boundaries tested; trade-off vs streaming documented in README. Known residual: across runs the stored value wins, so the outcome depends on file processing order for cross-run conflicts; this is a within-scope exception recorded in Complexity Tracking (not a within-run order dependence). |
| IV | Rules are data, engine open for extension | PASS | rules.json loaded at startup; `OperatorRegistry` built from DI (R10); new operator = class + registration, proven by a test with a fake operator; all seven operators; fail-fast `RuleCatalog`; no-rule-applies ⇒ acceptable. |
| V | Alerts represent episodes | PASS | Operators return episodes; `AlertPolicy` is a separate domain service; cooldown anchored on previous raised alert's `startTs` (R9); alert key `(ruleId, deviceId, metric, startTs)`. |
| VI | Idempotency by construction (NON-NEGOTIABLE) | PASS | Natural-key primary keys + `ON CONFLICT DO NOTHING`; delete-and-reinsert per affected series inside one transaction (R4); no check-then-insert; re-run test at Application level (fakes) **and** Infrastructure level (real SQLite). |
| VII | Test-first, behavior-focused (NON-NEGOTIABLE) | PASS | Test list in "Testing Strategy" covers every mandatory test named in the constitution; tasks will order each test before its implementation; `FakeTimeProvider`, no wall-clock, no randomness (shuffle uses a fixed seed). |
| VIII | Aggregation uses only acceptable data | PASS | Query filters `classification='A'`, `[from, to)`, buckets aligned to `from`, empty buckets with count 0 / null stats, typed 400 results (R11). |
| IX | Observability and honest reporting | PASS | `ProcessingReport` with invariant methods asserted in tests; log levels per spec; Debug guarded; report printed before the API serves. See R7 for how counts are scoped in multi-file runs. |
| X | Clean, simple, explainable code | PASS | Records/value objects, named constants and options, small services, no MediatR/AutoMapper/ORM; minimal package set (R13). |

Delivery standards: incremental conventional commits per slice (see Delivery Slices); README
sections enumerated below; AI disclosure included.

**Observations (not violations)**

- *FR-012 and the optional endpoints.* FR-012 requires acceptable and unacceptable readings to be
  *available as separate outputs*. This plan satisfies it at the query-port level
  (`IReadingQuery`) and in storage (`classification` column + `rule_results`), tested in
  Application/Infrastructure tests. The two read-only endpoints (`/api/alerts`,
  `/api/readings/unacceptable`) expose it over HTTP and stay marked optional in tasks; the
  endpoint set is not rule-management CRUD, so Principle I's out-of-scope list is not touched.
- *Report scoping (R7)* is a plan-level interpretation of a case the spec did not cover
  (multi-file runs); it does not change single-file behavior.

**Post-design re-check**: Phase 1 artifacts introduce no new violation. The one design choice that
could have strained Principle I, validating wrong JSON types without a JSON dependency in Domain,
is resolved by R1.

## Project Structure

### Documentation (this feature)

```text
specs/001-sensor-ingestion-alerting/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/
│   ├── openapi.yaml     # Aggregation endpoint (+ optional read-only endpoints)
│   └── rules.schema.json
├── checklists/requirements.md
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
SensorGuard.slnx
Directory.Build.props                  # net10.0, Nullable, TreatWarningsAsErrors, ImplicitUsings off
src/
├── SensorGuard.Domain/                # no package references
│   ├── Model/        (DeviceId, Metric, ReadingKey, Reading, SeriesKey, RuleId, Classification, Alert, Episode, RuleViolation)
│   ├── Ingestion/    (Field, RawReading, RejectionReason, Rejection, ReadingValidator, Deduplicator)
│   ├── Rules/        (Rule, RuleDefinition, RuleParameters, IStatelessOperator, IStatefulOperator, OperatorRegistry, RuleCatalog)
│   │   └── Operators/ (GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Equal, Between, SustainedAbove)
│   ├── Evaluation/   (SeriesEvaluator, SeriesEvaluation, AlertPolicy)
│   ├── Aggregation/  (AggregationCalculator, AggregationBucket)
│   └── Reporting/    (ProcessingReport + invariant methods)
├── SensorGuard.Application/           # Microsoft.Extensions.Logging.Abstractions, Options
│   ├── Ports/        (IReadingSource, IRuleSource, IReadingStore, IRuleResultStore, IAlertStore, IReadingQuery, IUnitOfWork)
│   ├── ProcessReadingsFile.cs
│   ├── ReportFormatter.cs  (+ IReportSink port in Ports/)
│   ├── GetAggregation.cs  (+ AggregationQuery, AggregationResult)
│   └── SensorGuardOptions.cs
├── SensorGuard.Infrastructure/        # Microsoft.Data.Sqlite, System.Text.Json
│   ├── Files/        (JsonlReadingSource, JsonRuleSource)
│   └── Sqlite/       (SqliteSchema, SqliteSession, SqliteUnitOfWork, SqliteReadingStore, SqliteRuleResultStore, SqliteAlertStore, SqliteReadingQuery, TimestampFormat)
└── SensorGuard.Api/                   # composition root
    ├── Program.cs     (DI, options, startup sequence, endpoint group)
    ├── ConsoleReportSink.cs
    ├── PathResolver.cs        (relative paths: working directory first, then next to the app)
    ├── Startup.cs             (startup sequence + StartupResult holding the startup report)
    ├── ServiceRegistration.cs (composition root wiring)
    ├── Endpoints/     (AggregationEndpoints, optional ReadModelEndpoints)
    └── appsettings.json
tests/
├── SensorGuard.Domain.Tests/          # includes ArchitectureTests
├── SensorGuard.Application.Tests/     # in-memory fakes
├── SensorGuard.Infrastructure.Tests/  # temp files, in-memory SQLite
└── SensorGuard.Api.Tests/             # WebApplicationFactory
data/                                  # sensor-data.json, rules.json (copied to output via config)
README.md
```

**Structure Decision**: the four-project Clean Architecture layout from the constitution, plus one
test project per layer. The architecture test lives in Domain.Tests (it only inspects assembly
metadata) so no fifth test project is needed.

### Key flows

**ProcessReadingsFile** (one `IUnitOfWork` transaction):
1. `IReadingSource` streams `RawLine`s; blank → counted; parse failure or `ReadingValidator`
   rejection → counted per reason, logged Warning; valid → `Reading`.
2. `Deduplicator` (identical copies collapse; conflicting copies resolved by lowest value, so file
   order is irrelevant) → kept readings, duplicate count, conflicting-duplicate warnings.
3. `IReadingStore.InsertIfAbsent` per kept reading → `Inserted` (NewlyStored), `AlreadyStored`, or
   `AlreadyStoredConflicting` (stored value kept, counted as AlreadyStored, Warning logged; R16).
4. Affected series = series of kept readings. `IReadingStore.LoadSeries` returns **all** stored
   readings for those series (the file's readings are now included).
5. `SeriesEvaluator` (with `RuleCatalog` rules) → classifications, violations, episodes; then
   `AlertPolicy` → raised/suppressed alerts. Application logs episodes, alerts, suppressions.
6. Persist: delete + insert `rule_results` and `alerts` for affected series; update
   `readings.classification`.
7. Build `ProcessingReport` (R7 scoping), log it (Information) and print it to the console.

**GetAggregation**: validate (typed errors) → bucket-count guard → `IReadingQuery.GetAcceptable`
→ `AggregationCalculator` → `AggregationResult`.

**Startup (Api)**: bind `SensorGuardOptions` → initialize schema → load and validate rules (fail
fast, non-zero exit) → `ProcessReadingsFile` → print report → `app.RunAsync()` (R6).

## Testing Strategy (test-first; red → green → refactor per slice)

| Project | Coverage (maps to constitution VII mandatory list) |
|---------|------------------------------------------------------|
| Domain.Tests | Validator: every reason code and precedence, timestamp accept/reject (Z, offset, fractional, no offset), blank deviceId, string value, negative seq, non-finite. Dedup: lowest-value-wins for conflicts (all permutations identical), conflicting flag, same ts different seq. Operators: each stateless operator incl. Between bounds and Equal exactness. Applicability: device-specific, all-device, other metric, disabled. SustainedAbove: just below / exactly at duration, `==` threshold ends episode, open at end, shuffled input identical, seq tie-break, independent series, gap not splitting. Alert policy: inside/outside cooldown (boundary equal raises), independent keys, anchor on raised alert's startTs. Aggregation: boundary on bucket start, `to` excluded, acceptable-only, empty buckets, alignment to `from`. Report invariants. Registry: fake operator added without production edits. Architecture test. |
| Application.Tests | Pipeline with fakes: shuffled file (incl. conflicting duplicates) ⇒ identical stores; stored-value conflict across runs ⇒ stored value kept, AlreadyStored, Warning; re-run ⇒ unchanged counts and `newly = 0`; report reconciliation on a mixed fixture; late earlier-timestamp file ⇒ series re-evaluated, no orphans; log levels asserted with a capturing logger. Aggregation validation (each 400 case, 10,000-bucket cap). |
| Infrastructure.Tests | JSONL source: BOM, CRLF, blank lines, no trailing newline, malformed, non-object, string value, `1e999`. Rule source: valid file, each invalid-rule failure message names the rule. SQLite stores on in-memory connection: PK conflict ⇒ no duplicate, delete-reinsert replaces stale rows, timestamp ordering of fixed-width text. |
| Api.Tests | `WebApplicationFactory` with temp DB: 200 with correct buckets, empty buckets, unknown device ⇒ 200 empty, each 400 case as ProblemDetails; end-to-end on the supplied `sensor-data.json` asserting report invariants and re-run idempotency. |

Determinism: fixtures are literals; the shuffle test uses a fixed seed; no `DateTime.UtcNow`.

## Delivery Slices (conventional commits)

1. `chore: scaffold solution, projects, Directory.Build.props, architecture test`
2. `feat(domain): reading validation and order-independent deduplication` (US1)
3. `feat(domain): rule model, operator registry, stateless operators, rule catalog` (US2)
4. `feat(domain): SustainedAbove, series evaluator, alert policy` (US3)
5. `feat(infra): JSONL source, rules loader, SQLite schema and stores` (US4 foundations)
6. `feat(app): ProcessReadingsFile, report, idempotent re-run` (US4, US6)
7. `feat(api): aggregation endpoint and composition root` (US5)
8. `docs: README` and optional read-only endpoints if time allows

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| System.Text.Json number edge cases (`1e999`, `-0`, big integers for seq) | Pinned by Infrastructure tests first (R1 Verify) |
| Supplied `rules.json` differs from the flat shape assumed in clarification 18 | Loader isolates the shape; schema file updated; no Domain change |
| Two-day budget | Optional endpoints last; no extra infrastructure; slices are independently demonstrable |
| Multi-file report semantics | Documented (R7); single-file path unaffected |

## Complexity Tracking

One justified exception is recorded. The four-project structure and the extra test projects are
mandated by Principle I/VII, not deviations.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Principle III (partial, waivable): when the same reading key arrives with different values in two separate runs, the stored value wins, so the outcome depends on which file ran first. Within a single run the result is fully order-independent. | Keeps ingestion pure insert-if-absent, which is the database-enforced idempotency mechanism required by non-negotiable Principle VI, and avoids an UPDATE path. Documented in the README and logged as a Warning on every conflict (research R16). | "Lowest value wins across runs" needs an UPDATE on stored readings, a "replaced" counter and re-evaluation triggers, weakening the insert-if-absent guarantee. "Reject both copies" drops data and adds a reason code. Neither is justified for a single-file task. |
