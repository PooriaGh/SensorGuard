---

description: "Task list for Sensor Ingestion, Stateful Rule Evaluation & Alerting"
---

# Tasks: Sensor Ingestion, Stateful Rule Evaluation & Alerting

**Input**: Design documents from `/specs/001-sensor-ingestion-alerting/`

**Prerequisites**: plan.md, spec.md, research.md (R1-R15), data-model.md, contracts/openapi.yaml, contracts/rules.schema.json, quickstart.md

**Tests**: REQUIRED. Constitution Principle VII (test-first, NON-NEGOTIABLE) makes them mandatory. Inside every story phase, write the test tasks first and watch them FAIL (red) before starting the implementation tasks (green), then refactor.

**Organization**: Tasks are grouped by user story. Domain-level logic for US1-US3 is independently testable with no storage; the persistence pipeline arrives in US4, which US5 and US6 build on (see Dependencies).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: US1-US6, mapping to the spec's user stories
- Paths are relative to the repository root. Namespaces mirror folders (`SensorGuard.Domain.Ingestion`, ...).
- Style for all code: C# 12, file-scoped namespaces, nullable enabled, warnings as errors, records/value objects in Domain, named constants (no magic numbers), plain constructor injection, no MediatR/AutoMapper/ORM/generic repository.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Solution skeleton with inward-only references. Nothing here contains logic.

- [X] T001 Run `git init` at the repository root and add `.gitignore` (bin/, obj/, *.db, *.db-journal, .vs/) so the incremental, conventional-commit history required by the constitution can exist; then make four separate commits so history shows the spec-kit flow: `docs: ratify constitution v1.0.0` (`.specify/`, `.claude/`), `docs: add feature specification and clarifications` (spec.md, checklists/), `docs: add implementation plan and design artifacts` (plan.md, research.md, data-model.md, contracts/, quickstart.md), `docs: add task breakdown` (tasks.md)
- [X] T002 Create `SensorGuard.slnx` and `Directory.Build.props` (TargetFramework net10.0, Nullable enable, ImplicitUsings disable, TreatWarningsAsErrors true, AnalysisLevel latest)
- [X] T003 [P] Create `src/SensorGuard.Domain/SensorGuard.Domain.csproj` with NO PackageReference and NO ProjectReference
- [X] T004 [P] Create `src/SensorGuard.Application/SensorGuard.Application.csproj` referencing Domain; PackageReference `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.Options`
- [X] T005 [P] Create `src/SensorGuard.Infrastructure/SensorGuard.Infrastructure.csproj` referencing Application (and Domain); PackageReference `Microsoft.Data.Sqlite`
- [X] T006 [P] Create `src/SensorGuard.Api/SensorGuard.Api.csproj` (Microsoft.NET.Sdk.Web) referencing all three; stub `Program.cs` ending with `public partial class Program;` so tests can use `WebApplicationFactory<Program>`; stub `appsettings.json`
- [X] T007 [P] Create `tests/SensorGuard.Domain.Tests/SensorGuard.Domain.Tests.csproj` (xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, Shouldly) referencing Domain only
- [X] T008 [P] Create `tests/SensorGuard.Application.Tests/SensorGuard.Application.Tests.csproj` (same packages plus `Microsoft.Extensions.TimeProvider.Testing`) referencing Application and Domain
- [X] T009 [P] Create `tests/SensorGuard.Infrastructure.Tests/SensorGuard.Infrastructure.Tests.csproj` (same packages) referencing Infrastructure, Application, Domain
- [X] T010 [P] Create `tests/SensorGuard.Api.Tests/SensorGuard.Api.Tests.csproj` (same packages plus `Microsoft.AspNetCore.Mvc.Testing`) referencing Api
- [X] T011 Add all eight projects to `SensorGuard.slnx` and verify `dotnet build SensorGuard.slnx -warnaserror` and `dotnet test SensorGuard.slnx` succeed on the empty skeleton
- [X] T012 Commit: `chore: scaffold solution with Clean Architecture projects`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared types and guards every story depends on.

**CRITICAL**: No user story work can begin until this phase is complete.

- [X] T013 [P] Write `tests/SensorGuard.Domain.Tests/ArchitectureTests.cs`: (a) Domain assembly's `GetReferencedAssemblies()` contains none of `SensorGuard.Application`, `SensorGuard.Infrastructure`, `SensorGuard.Api`, `Microsoft.Data.Sqlite`, `Microsoft.AspNetCore*`, `System.Text.Json`, `Microsoft.Extensions.Logging*`; (b) the Domain csproj text has no `PackageReference`; (c) Application does not reference Infrastructure or Api (Principle I)
- [X] T014 [P] Write `tests/SensorGuard.Domain.Tests/Reporting/ProcessingReportTests.cs` covering each invariant method: `LinesRead == BlankLines + Parsed + InvalidRejected`; `InvalidRejected == InvalidByReason.Values.Sum()`; `Parsed == NewlyStored + AlreadyStored + FileDuplicates`; `NewlyStored + AlreadyStored == Acceptable + Unacceptable`; plus failing cases for each (Principle IX)
- [X] T015 [P] Create `tests/SensorGuard.Domain.Tests/Support/Readings.cs`: tiny test builders (`Temp("PUMP-01", "2025-06-01T08:00:00Z", seq, value)`, ordering and shuffle helper with a FIXED seed) used by all domain tests, plus `tests/SensorGuard.Domain.Tests/Model/ModelTests.cs` written BEFORE T016: `ReadingKey` normalizes to UTC and rejects "Seq >= 0" violations, `Reading` rejects non-finite "Value finite", metric parsing is lowercase and case-sensitive, `DeviceId` rejects empty and whitespace
- [X] T016 Create Domain value objects (after T015's ModelTests are red) in `src/SensorGuard.Domain/Model/`: `DeviceId.cs` (`readonly record struct`, non-empty and non-whitespace, ordinal case-sensitive), `Metric.cs` (enum Temperature, Pressure, Vibration, with `MetricNames` parse/format using lowercase names only, case-sensitive), `ReadingKey.cs` (`record (DeviceId, Metric, DateTimeOffset Timestamp, long Seq)`, Timestamp normalized to UTC offset zero, "Seq >= 0"), `Reading.cs` (`record (ReadingKey Key, double Value)`, "Value finite", with `Series` derived), `SeriesKey.cs`, `RuleId.cs`, `Classification.cs` (Acceptable, Unacceptable), and `Ingestion/RejectionReason.cs` (MalformedJson, MissingField, InvalidType, UnknownMetric, InvalidTimestamp, InvalidSeq, NonFiniteValue)
- [X] T017 Implement `src/SensorGuard.Domain/Reporting/ProcessingReport.cs`: immutable record with the fields listed in data-model.md (LinesRead, BlankLines, Parsed, InvalidRejected, InvalidByReason, FileDuplicates, AlreadyStored, NewlyStored, RulesLoadedTotal, RulesLoadedEnabled, RuleEvaluations, Acceptable, Unacceptable, RuleViolations, AlertsGenerated, AlertsSuppressed, Elapsed) and the invariant methods from T014 (depends on T014, T016)
- [X] T018 [P] Create `src/SensorGuard.Application/SensorGuardOptions.cs`: InputFilePath, RulesFilePath, DatabasePath, `AlertCooldown` (default `TimeSpan.FromMinutes(5)` as a named constant), `MaxAggregationBuckets` (default 10000 as a named constant); section name constant `SensorGuard`
- [X] T019 `data/sensor-data.json` (copy of `docs/sensor-data.json`, JSON Lines despite the extension) and the authored seed `data/rules.json` (11 rules covering all seven operators, one disabled; the task shipped no rules file) already exist; verify them, keep the original in `docs/`, configure `src/SensorGuard.Api/SensorGuard.Api.csproj` to copy `data/*` to the output via `<Content Include="..\..\data\*" CopyToOutputDirectory="PreserveNewest" />` (not embedded), copy both to `tests/SensorGuard.Api.Tests/Fixtures/` as test fixtures (T074 reads only these), and set `appsettings.json` defaults for `SensorGuard` options (`InputFilePath` = `data/sensor-data.json`, `RulesFilePath` = `data/rules.json`). Validate `data/rules.json` against `contracts/rules.schema.json`
- [X] T020 Verify `dotnet test SensorGuard.slnx` is green (architecture and report tests) and commit: `feat(foundation): value objects, processing report invariants, architecture test`

**Checkpoint**: Foundation ready; US1, US2 and US3 domain work can proceed in parallel, US4 needs all three.

---

## Phase 3: User Story 1 - Clean ingestion of messy data (Priority: P1) MVP

**Goal**: Stream a JSONL file, reject invalid lines with a reason code, remove duplicates order-independently (lowest value wins for conflicts, R16), never throw, never abort.

**Independent Test**: Run `ReadingValidatorTests`, `DeduplicatorTests` and `JsonlReadingSourceTests` (no storage, no rules). A fixture with a duplicate, each invalid kind, blank lines, BOM and CRLF yields the expected kept readings, duplicate count and per-reason counts.

### Tests for User Story 1 (write first, confirm they FAIL)

- [X] T021 [P] [US1] `tests/SensorGuard.Domain.Tests/Ingestion/ReadingValidatorTests.cs`: one test per reason code (MalformedJson for not-an-object is produced by the source, so test `MissingField` for null/absent/empty/whitespace deviceId, `InvalidType` for string `value` and numeric `deviceId`, `UnknownMetric` for `Temperature` (wrong case) and `humidity`, `InvalidTimestamp` for no-offset and garbage text, a non-string `ts` is `InvalidType`, `NonFiniteValue` for +/-Infinity, `InvalidSeq` for -1, 1.5, `5.0` (only integer tokens are accepted) and a seq above `long.MaxValue`); accept `Z`, `+02:00`, fractional seconds and normalize to UTC; negative vibration is valid; precedence per research R2 when several defects exist
- [X] T022 [P] [US1] `tests/SensorGuard.Domain.Tests/Ingestion/DeduplicatorTests.cs`: identical-value copies collapse to one with the rest counted; conflicting copies (same key, different value) keep the LOWEST value and report kept and ignored values; the result is identical for every permutation of the input (assert over all orderings of a 3-copy case); same ts with different seq are distinct, identical value duplicate is not conflicting
- [X] T023 [P] [US1] `tests/SensorGuard.Infrastructure.Tests/Files/JsonlReadingSourceTests.cs` using temp-file fixtures: UTF-8 BOM, CRLF, LF, missing trailing newline, blank and whitespace-only lines flagged `IsBlank` with correct line numbers, malformed JSON line yields `MalformedJson` (no exception), JSON array/scalar line yields `MalformedJson`, `"value": "12"` yields a String field, `1e999` yields Number field with non-finite value, `"seq": 5.0` yields a Number field with `IsIntegerToken == false`, a seq above `long.MaxValue` yields a non-integer-token Number, empty file yields no lines, 1-based line numbers

### Implementation for User Story 1

- [X] T024 [P] [US1] Create `src/SensorGuard.Domain/Ingestion/Field.cs` (Kind Missing/Null/String/Number/Other, Text, Number, IsIntegerToken) and `RawReading.cs` (LineNumber plus five Fields), and `Rejection.cs` (LineNumber, RejectionReason, Detail) per data-model.md
- [X] T025 [P] [US1] Create `src/SensorGuard.Domain/Ingestion/TimestampParser.cs`: accept only ISO-8601 date-times ending in `Z` or `+hh:mm`/`-hh:mm` with optional fractional seconds via explicit-format `DateTimeOffset.TryParseExact`, return UTC; no offset means failure (research R3)
- [X] T026 [US1] Implement `src/SensorGuard.Domain/Ingestion/ReadingValidator.cs`: `Validate(RawReading) -> ValidationResult` (Reading or Rejection) with the precedence in research R2, no exceptions for data errors (depends on T024, T025, T016)
- [X] T027 [P] [US1] Implement `src/SensorGuard.Domain/Ingestion/Deduplicator.cs`: group by `ReadingKey`, keep the lowest value per key (research R16; no dependence on input order), return kept readings sorted by `(Timestamp, Seq)`, the duplicate count and a `ConflictingDuplicate(ReadingKey, KeptValue, IgnoredValues)` list
- [X] T028 [US1] Create port `src/SensorGuard.Application/Ports/IReadingSource.cs` with `IAsyncEnumerable<RawLine> ReadAsync(CancellationToken)` and `RawLine.cs` (LineNumber, `IsBlank`, `RawReading? Parsed`, `RejectionReason? ParseFailure`) per data-model.md
- [X] T029 [US1] Implement `src/SensorGuard.Infrastructure/Files/JsonlReadingSource.cs`: stream via `StreamReader` (BOM-tolerant), parse each non-blank line with `JsonDocument`/`Utf8JsonReader`, map JSON kinds to `Field`, mark integer tokens, convert any number that is not a finite double to a non-finite Number field, catch `JsonException` into `ParseFailure = MalformedJson` (research R1)
- [X] T030 [US1] Run the US1 tests green, refactor, and commit: `feat(domain,infra): reading validation, order-independent deduplication and JSONL source`

**Checkpoint**: US1 is fully testable without rules, storage or API.

---

## Phase 4: User Story 2 - Rule-based classification (Priority: P1)

**Goal**: Rules from data, six stateless operators, applicability, fail-fast validation, pluggable operators, classification as the union of violations.

**Independent Test**: `StatelessOperatorsTests`, `RuleApplicabilityTests`, `RuleCatalogTests`, `OperatorRegistryTests`, `SeriesEvaluatorStatelessTests` and `JsonRuleSourceTests` pass with in-memory readings and a temp `rules.json`.

### Tests for User Story 2 (write first, confirm they FAIL)

- [X] T031 [P] [US2] `tests/SensorGuard.Domain.Tests/Rules/StatelessOperatorsTests.cs`: GreaterThan fires at `v > value` (not at equality); GreaterThanOrEqual at equality; LessThan; LessThanOrEqual; Equal exact (no tolerance, `0.1+0.2 != 0.3`); Between is the ONLY rule describing the acceptable range: violation when `v < min || v > max`, `v == min` and `v == max` are acceptable; reason text is human-readable (e.g. `temperature GreaterThan 100 fires at 103.2`)
- [X] T032 [P] [US2] `tests/SensorGuard.Domain.Tests/Rules/RuleApplicabilityTests.cs`: rule with deviceId applies only to that device; rule without deviceId applies to all devices of that metric; never to another metric; disabled rule never applies
- [X] T033 [P] [US2] `tests/SensorGuard.Domain.Tests/Rules/RuleCatalogTests.cs`: each startup failure names the offending rule: duplicate id, unknown operator (case-insensitive match otherwise accepted), unknown metric, missing parameter per operator, non-finite parameter, `min > max`, `durationSeconds <= 0`; a rule for a device with no data is valid
- [X] T034 [P] [US2] `tests/SensorGuard.Domain.Tests/Rules/OperatorRegistryTests.cs`: case-insensitive lookup, duplicate operator names rejected, and a test-local fake `IStatelessOperator` registered WITHOUT editing any production class is resolved and evaluated through `RuleCatalog`/`SeriesEvaluator` (proves Open/Closed, FR-016)
- [X] T035 [P] [US2] `tests/SensorGuard.Domain.Tests/Evaluation/SeriesEvaluatorStatelessTests.cs`: no applicable rule means Acceptable; two violated rules both reported (union); disabled rule never evaluated; reading classified per applicable rules only; rule-evaluation count equals readings times applicable enabled stateless rules
- [X] T036 [P] [US2] `tests/SensorGuard.Infrastructure.Tests/Files/JsonRuleSourceTests.cs`: loads the example in `contracts/rules.schema.json`, operator names in any casing, optional deviceId, parameters read from top-level properties into `RuleParameters`, malformed rules.json and missing file fail with a clear message

### Implementation for User Story 2

- [X] T037 [US2] Create `src/SensorGuard.Domain/Rules/`: `RuleParameters.cs` (read-only `string -> double` map), `ValidatedParameters.cs`, `IStatelessOperator.cs` and `IStatefulOperator.cs` with the exact members in data-model.md, `OperatorKind.cs`
- [X] T038 [P] [US2] Create the six operators in `src/SensorGuard.Domain/Rules/Operators/`: `GreaterThanOperator.cs`, `GreaterThanOrEqualOperator.cs`, `LessThanOperator.cs`, `LessThanOrEqualOperator.cs`, `EqualOperator.cs`, `BetweenOperator.cs`; each validates its required parameters (`value`; `min` and `max`) and returns readable `Describe` text
- [X] T039 [P] [US2] Create `src/SensorGuard.Domain/Rules/RuleDefinition.cs` and `Rule.cs` with `AppliesTo(SeriesKey)`: `Enabled && Metric == series.Metric && (DeviceId is null || DeviceId == series.Device)`
- [X] T040 [US2] Implement `src/SensorGuard.Domain/Rules/OperatorRegistry.cs`: built from `IEnumerable<IStatelessOperator>` and `IEnumerable<IStatefulOperator>`, case-insensitive dictionaries, duplicate names throw at construction (depends on T037)
- [X] T041 [US2] Implement `src/SensorGuard.Domain/Rules/RuleCatalog.cs`: converts `RuleDefinition`s to validated `Rule`s, fail fast with errors naming the rule (id and name), checks listed in T033 (depends on T038, T039, T040)
- [X] T042 [US2] Implement the stateless half of `src/SensorGuard.Domain/Evaluation/SeriesEvaluator.cs` and `SeriesEvaluation.cs`, `ReadingClassification.cs`, `RuleViolation.cs`: group by `SeriesKey`, sort by `(Timestamp, Seq)`, apply applicable enabled stateless rules per reading, count rule evaluations; signature `Evaluate(IEnumerable<Reading>, IReadOnlyList<Rule>)` already returns an `Episodes`/`Alerts` slot left empty for US3 (depends on T041)
- [X] T043 [P] [US2] Create port `src/SensorGuard.Application/Ports/IRuleSource.cs` (`Task<IReadOnlyList<RuleDefinition>> LoadAsync`) and implement `src/SensorGuard.Infrastructure/Files/JsonRuleSource.cs` (System.Text.Json, flat parameters, no domain validation here)
- [X] T044 [US2] Run US2 tests green, refactor, commit: `feat(domain,infra): rule model, operator registry, stateless operators, rule catalog`

**Checkpoint**: US1 and US2 both work independently; readings can be validated and classified in memory.

---

## Phase 5: User Story 3 - Sustained-condition detection with alerts (Priority: P1)

**Goal**: Event-time SustainedAbove scan, reading-level unacceptability inside sustained episodes, one cooldown-deduplicated alert per episode.

**Independent Test**: `SustainedAboveTests`, `AlertPolicyTests` and `SeriesEvaluatorStatefulTests` pass; the same readings in sorted and shuffled order produce identical alerts and classifications.

### Tests for User Story 3 (write first, confirm they FAIL)

- [X] T045 [P] [US3] `tests/SensorGuard.Domain.Tests/Rules/SustainedAboveTests.cs`: duration one second below and exactly equal to `durationSeconds` (equal is sustained); value equal to threshold is not above and ends the episode; single above reading followed by a non-above reading uses the gap as duration; episode still open at end of data ends at last reading's ts with `OpenAtEnd = true`; gap between two above readings does not split the episode (hold-last-value); peak is the max value in the episode; two episodes in one series; ties on ts ordered by seq; covered readings are exactly those with ts in `[startTs, endTs)`; the ending reading is excluded; sub-duration runs return no episode; independent series do not interact; a series with a single reading; readings years apart in one series (no special handling, hold-last-value applies)
- [X] T046 [P] [US3] `tests/SensorGuard.Domain.Tests/Evaluation/AlertPolicyTests.cs`: second episode inside the cooldown is suppressed; episode with `startTs - previous.startTs` exactly equal to the cooldown raises (strict `<` suppresses); anchored on the previous RAISED alert's startTs (a suppressed episode does not move the anchor); independent per `(ruleId, deviceId, metric)`; configurable cooldown; alert carries ruleId, ruleName, deviceId, metric, startTs, endTs, peakValue, OpenAtEndOfData; alert identity is `(ruleId, deviceId, metric, startTs)`
- [X] T047 [P] [US3] `tests/SensorGuard.Domain.Tests/Evaluation/SeriesEvaluatorStatefulTests.cs`: sorted vs shuffled (fixed seed) input gives identical classifications and alerts (SC-002); sustained and stateless rules together give the union of violations; readings in a suppressed episode are still Unacceptable; reason text like `above 80 for 30s+ (episode started 2025-06-01T08:30:00Z)`; a sustained rule applies per series for `deviceId`-less rules; rule-evaluation count adds one per sustained rule per series

### Implementation for User Story 3

- [X] T048 [P] [US3] Create `src/SensorGuard.Domain/Model/Episode.cs`, `Alert.cs`, and `src/SensorGuard.Domain/Evaluation/AlertDecision.cs` (raised alerts plus suppressed episodes with the suppressing alert) per data-model.md
- [X] T049 [P] [US3] Implement `src/SensorGuard.Domain/Rules/Operators/SustainedAboveOperator.cs` as `IStatefulOperator`: validates `threshold` (finite) and `durationSeconds` ("> 0"); single-pass scan from research R8, returns only sustained episodes with covered reading keys
- [X] T050 [US3] Implement `src/SensorGuard.Domain/Evaluation/AlertPolicy.cs`: `Apply(IEnumerable<Episode>, TimeSpan cooldown)` per research R9, deterministic order by startTs (depends on T048)
- [X] T051 [US3] Extend `src/SensorGuard.Domain/Evaluation/SeriesEvaluator.cs` to run applicable stateful rules per ordered series, mark covered readings Unacceptable with the episode reason, union with stateless violations, call `AlertPolicy`, and return alerts, suppressed episodes and the rule-evaluation count (depends on T049, T050)
- [X] T052 [US3] Run US3 tests green, refactor, commit: `feat(domain): SustainedAbove scan, series evaluator and alert cooldown policy`

**Checkpoint**: All P1 domain behavior works in memory: validate, dedupe, classify, alert, deterministic regardless of order.

---

## Phase 6: User Story 4 - Idempotent re-processing (Priority: P2)

**Goal**: Persist readings, rule results and alerts in SQLite under natural keys, run the whole pipeline in one transaction, and make re-runs change nothing.

**Independent Test**: `ProcessReadingsFileTests` (fakes) and `SqliteStoresTests` (in-memory SQLite) pass; running the pipeline twice yields identical row counts and content with `newly stored = 0` on the second run.

### Tests for User Story 4 (write first, confirm they FAIL)

- [X] T053 [P] [US4] Create Application test support in `tests/SensorGuard.Application.Tests/Support/`: `InMemoryReadingStore`, `InMemoryRuleResultStore`, `InMemoryAlertStore` (primary-key semantics identical to the SQL schema), `PassThroughUnitOfWork`, `FakeReadingSource` (from string lines), `CapturingLogger<T>`, and `FakeTimeProvider` usage
- [X] T054 [P] [US4] `tests/SensorGuard.Application.Tests/ProcessReadingsFileTests.cs`: mixed fixture (blank, invalid of several reasons, duplicate, conflicting duplicate, valid) produces expected stored/rejected counts; shuffled file (fixture includes a conflicting duplicate) gives identical stored readings, rule results and alerts; a reading already stored with a different value keeps the stored value, counts as AlreadyStored and logs a Warning; second run: stores unchanged in count AND content, `NewlyStored == 0`, `AlreadyStored ==` first run's `NewlyStored`, identical alerts; a later file with earlier-timestamped readings re-evaluates the affected series and leaves no orphan results or alerts; removing a rule between runs removes its stale results; invalid readings never produce rule results (FR-004)
- [X] T055 [P] [US4] `tests/SensorGuard.Infrastructure.Tests/Sqlite/SqliteStoresTests.cs` on an in-memory SQLite connection: reading primary key conflict means `INSERT ... ON CONFLICT DO NOTHING` returns `AlreadyStored` (same value) or `AlreadyStoredConflicting` (different value, stored value untouched); rule-result and alert keys reject duplicates; delete-and-reinsert per series replaces stale rows; fixed-width timestamp text sorts chronologically and preserves 7-digit fractions; unit of work rolls back everything when the body throws

### Implementation for User Story 4

- [X] T056 [P] [US4] Create ports in `src/SensorGuard.Application/Ports/`: `IReadingStore` (`InsertIfAbsentAsync(Reading) -> InsertOutcome` {Inserted, AlreadyStored, AlreadyStoredConflicting}, `LoadSeriesAsync(IEnumerable<SeriesKey>) -> IReadOnlyList<Reading>`, `UpdateClassificationsAsync`), `IRuleResultStore` (`ReplaceForSeriesAsync(SeriesKey, IEnumerable<RuleViolation>)`), `IAlertStore` (`ReplaceForSeriesAsync(SeriesKey, IEnumerable<Alert>)`), `IUnitOfWork` (`ExecuteAsync(Func<CancellationToken, Task>, CancellationToken)`)
- [X] T057 [P] [US4] Implement `src/SensorGuard.Infrastructure/Sqlite/SqliteSchema.cs` (DDL exactly as in data-model.md, run at startup, `PRAGMA foreign_keys=ON`) and `TimestampFormat.cs` (fixed-width `yyyy-MM-ddTHH:mm:ss.fffffffZ` format/parse, research R3)
- [X] T058 [US4] Implement `src/SensorGuard.Infrastructure/Sqlite/SqliteSession.cs` and `SqliteUnitOfWork.cs` (one connection and transaction per run, commit on success, rollback on exception; stores read the ambient session)
- [X] T059 [US4] Implement `SqliteReadingStore.cs`, `SqliteRuleResultStore.cs`, `SqliteAlertStore.cs` in `src/SensorGuard.Infrastructure/Sqlite/` with small hand-written parameterized SQL; reading insert uses `ON CONFLICT DO NOTHING`; when `changes() == 0` a primary-key `SELECT value` decides AlreadyStored vs AlreadyStoredConflicting (R16); replace-for-series is `DELETE` then `INSERT`; no generic repository (depends on T057, T058)
- [X] T060 [US4] Implement `src/SensorGuard.Application/ProcessReadingsFile.cs` following the plan's "Key flows": stream, validate, dedupe, insert-if-absent counting NewlyStored/AlreadyStored (Warning on AlreadyStoredConflicting), load all stored readings of affected series, `SeriesEvaluator`, persist classifications/results/alerts in ONE `IUnitOfWork`, build `ProcessingReport` with the scoping from research R7, time elapsed via injected `TimeProvider`; log rejections and conflicting duplicates as Warning, episodes, alerts and suppressions as Information (message templates), per-reading detail at Debug guarded by `IsEnabled` (depends on T056, T028, T042, T051)
- [X] T061 [US4] Implement the composition root in `src/SensorGuard.Api/Program.cs`: bind `SensorGuardOptions`, register `TimeProvider.System`, `OperatorRegistry` and ALL seven operators explicitly (one `AddSingleton<I...Operator, ...>()` line each), sources, stores and use cases; startup order per research R6: initialize schema, load and validate rules (on failure print the message and exit non-zero before touching the DB), run `ProcessReadingsFile`, then `app.RunAsync()`
- [X] T062 [US4] Run US4 tests green, refactor, commit: `feat(app,infra): idempotent ingestion pipeline with SQLite natural keys`

**Checkpoint**: The app ingests the supplied file, persists everything, and re-runs are no-ops.

---

## Phase 7: User Story 5 - Aggregation API (Priority: P2)

**Goal**: `GET /api/aggregations` returns buckets of acceptable readings aligned to `from` over `[from, to)`.

**Independent Test**: `AggregationCalculatorTests` and `GetAggregationTests` pass against hand-computed oracles; `AggregationEndpointTests` pass through `WebApplicationFactory`.

### Tests for User Story 5 (write first, confirm they FAIL)

- [X] T063 [P] [US5] `tests/SensorGuard.Domain.Tests/Aggregation/AggregationCalculatorTests.cs`: reading exactly on a bucket start belongs to that bucket; reading at `to` excluded; reading at `from` included; buckets aligned to `from` (not epoch) with `from` not on a round time; empty buckets have count 0 and null average/min/max; last bucket may extend past `to`; count, average (unrounded), min, max against a hand-computed oracle; only the readings handed in are counted
- [X] T064 [P] [US5] `tests/SensorGuard.Application.Tests/GetAggregationTests.cs`: each invalid input returns `Invalid` with a message naming the parameter (missing deviceId/metric/from/to/bucketSeconds, unparseable timestamp, timestamp with no offset, `from >= to`, `bucketSeconds <= 0`, unknown metric, more than `MaxAggregationBuckets` buckets, checked BEFORE any query); unknown device with valid metric returns Ok with all-empty buckets; unacceptable readings are excluded
- [X] T065 [P] [US5] `tests/SensorGuard.Infrastructure.Tests/Sqlite/SqliteReadingQueryTests.cs`: returns only classification `A`, only `from <= ts < to`, only the requested series, ordered by `(ts, seq)`; `GetUnacceptableAsync` returns only classification `U` with violated rule ids and reasons, optionally filtered by series (FR-012)
- [X] T066 [P] [US5] `tests/SensorGuard.Api.Tests/AggregationEndpointTests.cs` with `WebApplicationFactory<Program>`, temp database and temp fixture files: 200 with the JSON shape in `contracts/openapi.yaml` (empty buckets have null statistics), unknown device 200 empty, every 400 case returns `application/problem+json` naming the parameter

### Implementation for User Story 5

- [X] T067 [P] [US5] Implement `src/SensorGuard.Domain/Aggregation/AggregationBucket.cs` and `AggregationCalculator.cs`: pure over `IEnumerable<Reading>`, bucket index `floor((ts - from) / bucket)`, `Count == 0` means null average/min/max
- [X] T068 [P] [US5] Create port `src/SensorGuard.Application/Ports/IReadingQuery.cs` (`GetAcceptableAsync(SeriesKey, from, to)`, plus `GetUnacceptableAsync(SeriesKey?)` returning readings with violations for FR-012) and implement `src/SensorGuard.Infrastructure/Sqlite/SqliteReadingQuery.cs`
- [X] T069 [US5] Implement `src/SensorGuard.Application/GetAggregation.cs`, `AggregationQuery.cs`, `AggregationResult.cs` (typed Ok/Invalid, no exceptions) with validation per spec clarifications and the `MaxAggregationBuckets` guard (depends on T067, T068)
- [X] T070 [US5] Implement `src/SensorGuard.Api/Endpoints/AggregationEndpoints.cs`: Minimal API endpoint group `/api`, `GET /aggregations` maps query string to `AggregationQuery`, maps `Invalid` to `Results.ValidationProblem` (400, problem+json), `Ok` to the response record; no logic of its own; register in `Program.cs`
- [X] T071 [US5] Run US5 tests green, refactor, commit: `feat(api): acceptable-only time-bucketed aggregation endpoint`

**Checkpoint**: Clients can chart clean data; unacceptable readings never appear.

---

## Phase 8: User Story 6 - Processing report and observability (Priority: P3)

**Goal**: A reconciled report printed to the console and logged at the end of ingestion; correct log levels.

**Independent Test**: `ReportFormatterTests` and `ProcessReadingsFileLoggingTests` pass; the end-to-end test on the supplied `sensor-data.json` reconciles.

### Tests for User Story 6 (write first, confirm they FAIL)

- [X] T072 [P] [US6] `tests/SensorGuard.Application.Tests/ReportFormatterTests.cs`: output contains every report line (lines read, blank, parsed, invalid with per-reason breakdown, file duplicates, already stored, newly stored, rules loaded enabled/total, rule evaluations, acceptable, unacceptable, rule violations, alerts generated, alerts suppressed, elapsed) with the right numbers
- [X] T073 [P] [US6] `tests/SensorGuard.Application.Tests/ProcessReadingsFileLoggingTests.cs` with `CapturingLogger`: invalid record and conflicting duplicate log at Warning; rules loaded, episode detected, alert raised, suppressed alert and the report log at Information; a run of valid readings with Information enabled emits NO per-reading Information entries; per-reading detail appears only at Debug
- [X] T074 [P] [US6] `tests/SensorGuard.Api.Tests/SuppliedFileEndToEndTests.cs`: process the supplied files, used as fixtures copied to `tests/SensorGuard.Api.Tests/Fixtures/` (T019), into a temp database; assert all four report invariants, SC-001 (every line accounted for), SC-003 (second run: `NewlyStored == 0`, row counts unchanged), shuffled-copy file (seeded shuffle) gives identical alerts and classifications (SC-002)

### Implementation for User Story 6

- [X] T075 [US6] Implement `src/SensorGuard.Application/ReportFormatter.cs` (plain text, stable ordering) and call it from `ProcessReadingsFile` completion path: write to the console through an `IReportSink` port (`ConsoleReportSink` in `src/SensorGuard.Api/ConsoleReportSink.cs`) and emit one structured Information log entry with named properties; check the `report` invariants on every run (all builds) and log a Warning naming the broken invariant if one fails
- [X] T076 [US6] Run US6 tests green, refactor, commit: `feat(app): reconciled processing report and log-level policy`

**Checkpoint**: All six stories complete and demonstrable.

---

## Phase 9: Polish & Cross-Cutting Concerns

- [X] T077 [P] (OPTIONAL, only if under one hour) Add read-only `GET /api/alerts` and `GET /api/readings/unacceptable` in `src/SensorGuard.Api/Endpoints/ReadModelEndpoints.cs` per `contracts/openapi.yaml` with Api tests in `tests/SensorGuard.Api.Tests/ReadModelEndpointTests.cs` (tests first); skip if time is short, FR-012 is already met at the `IReadingQuery` level
- [X] T078 [P] Write `README.md` covering every item in the plan's README Plan: how to run and test; architecture and trade-offs; SQLite choice; rule model with "every rule describes the violating condition, except Between which describes the acceptable range"; evaluation policy and no-applicable-rule policy; batch vs streaming trade-off and late arrivals across runs; SustainedAbove state and ordering strategy; cooldown policy and anchor; duplicate policy (lowest value for in-file conflicts, stored value across runs and its order-dependence limitation); idempotency keying table (reading, rule result, alert); aggregation behavior including empty buckets and `from`-aligned buckets; report scoping (R7); "Adding an operator" in three steps; known limitations; a note that `data/rules.json` is authored by the project (the task shipped none) and what each rule demonstrates; AI tool usage disclosure
- [X] T079 [P] Constitution audit: `Grep` the solution for `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `Random(` without a seed, and `Thread.Sleep` in src/ and tests/ and remove any hit; search for magic numbers and dead code; confirm Domain csproj still has no references (Principles I, VII, X)
- [X] T080 Execute every scenario in `specs/001-sensor-ingestion-alerting/quickstart.md` against the real app, correct the document where actual output differs, and record the supplied-file report numbers in the README
- [X] T081 Measure end-to-end processing time of the supplied file (SC-006, expect seconds) and note it in the README; if slow, profile before optimizing
- [X] T082 Final full run: `dotnet build -warnaserror`, `dotnet test`, then commit `docs: README and final verification`

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (Phase 1)** then **Foundational (Phase 2)** block everything.
- **US1, US2, US3** are domain-only and mostly independent: US1 and US2 can run in parallel after Phase 2. US3 depends on US2 (it extends `SeriesEvaluator`, T051 after T042) and on the value objects only otherwise.
- **US4** depends on US1 (source, validator, deduplicator), US2 (rules, evaluator) and US3 (alerts).
- **US5** depends on US4 for persisted, classified data; its pure parts (T063, T067) can be done any time after Phase 2.
- **US6** depends on US4 (the pipeline produces the report); `ProcessingReport` itself is foundational.
- **Polish** depends on all stories; T077 is optional.

### Within each story

Tests first and failing, then models, then services, then ports and adapters, then endpoints; commit at the story checkpoint.

### Parallel opportunities

- Setup: T003-T010 in parallel after T002.
- Foundational: T013, T014, T015, T018 in parallel; T016 only after T015 is red.
- US1: T021-T023 together; then T024, T025, T027 together.
- US2: T031-T036 together; T038, T039, T043 together after T037.
- US3: T045-T047 together; T048 and T049 together.
- US4: T053-T055 together; T056 and T057 together.
- US5: T063-T066 together; T067 and T068 together.
- US6: T072-T074 together.
- Cross-story: US1 and US2 by different people or sessions; pure aggregation (T063, T067) alongside US3.

### Parallel example: User Story 1

```text
Task: T021 ReadingValidatorTests.cs        (tests/SensorGuard.Domain.Tests/Ingestion/)
Task: T022 DeduplicatorTests.cs            (tests/SensorGuard.Domain.Tests/Ingestion/)
Task: T023 JsonlReadingSourceTests.cs      (tests/SensorGuard.Infrastructure.Tests/Files/)
```

---

## Implementation Strategy

### MVP first

1. Phases 1-2 (skeleton, foundation).
2. US1 + US2 + US3: the complete, deterministic, in-memory core. Stop and demo with unit tests: this is the technical heart of the task.
3. US4 makes it a working, idempotent service (first demonstrable end-to-end run). Treat Phases 1-6 as the MVP.

### Incremental delivery

Each story ends with a green test suite and one conventional commit (T012, T020, T030, T044, T052, T062, T071, T076, T082), so the history follows the spec-kit flow: constitution, spec, plan, tasks, then implementation by slice.

### Scope control

Per the constitution, if any task threatens the two-day budget, raise it immediately instead of silently dropping it. First candidates to defer: T077 (optional endpoints), then T081. Never defer tests, the architecture test, or the README sections that document decisions (SC-007).

---

## Notes

- [P] tasks touch different files and have no dependency on an incomplete task.
- Verify each test fails before implementing (red), then pass (green), then refactor.
- Do not add message brokers, a generic repository, MediatR, AutoMapper, an ORM, authentication, a UI or rule-management endpoints.
- Tests never use wall-clock time or unseeded randomness; use `FakeTimeProvider` and fixed seeds.

---

## Phase 10: Convergence

- [X] T083 Add an Api startup test in tests/SensorGuard.Api.Tests/StartupFailFastTests.cs: a rules file with an unknown operator, a duplicate id and min > max makes Startup.RunIngestionAsync return null, creates no database file at the configured DatabasePath, and logs a Critical message naming every offending rule; a missing input file also returns null with a clear message per US2/AC6 (partial)
- [X] T084 [P] Add tests/SensorGuard.Api.Tests/PathResolverTests.cs for PathResolver (absolute path returned unchanged, existing working-directory path wins, fallback to the application directory, unknown path returned unchanged), and record PathResolver and StartupResult in the Api section of the plan source tree or remove them, per plan: source tree (unrequested)
- [X] T085 [P] Replace the raw classification literals "A" and "U" in src/SensorGuard.Infrastructure/Sqlite/SqliteDatabase.cs and SqliteStores.cs with one named constants class (for example ClassificationCodes) used by the schema CHECK, the update and both queries, per Constitution X (partial)

---

## Phase 11: Convergence

- [X] T086 Add `*.db`, `*.db-shm`, `*.db-wal` and `*.db-journal` to .gitignore and untrack the accidentally committed src/SensorGuard.Api/sensorguard.db (`git rm --cached`), so a run or test that uses the default relative DatabasePath never dirties the repository, per T001 git hygiene and Constitution Delivery (contradicts)
