# Phase 0 Research: Sensor Ingestion, Stateful Rule Evaluation & Alerting

All Technical Context items were supplied by the plan input, so no `NEEDS CLARIFICATION` remains.
This file records the decisions that were *not* fully dictated by the spec or the input, each
with rationale and rejected alternatives. Items marked **Verify** are behaviors of the platform that
the first red tests must pin down before the code relies on them.

## R1. Domain stays free of JSON: a neutral raw-record model

- **Decision**: Infrastructure parses each line with System.Text.Json and emits a domain-neutral
  `RawReading(LineNumber, Field DeviceId, Field Metric, Field Ts, Field Value, Field Seq)` or a
  `RawLineFailure(LineNumber, MalformedJson)`. A `Field` is a small record: `Kind`
  (`Missing | Null | String | Number | Other`), `Text` (for strings), `Number` (`double?`) and
  `IsIntegerToken` (true when the JSON number token has no fraction or exponent). `ReadingValidator`
  (Domain) maps this to `Reading` or `Rejection(ReasonCode)`.
- **Rationale**: The wrong-type rules (`value` as a string, `seq` as 1.5) can only be decided when the
  JSON kind is still known, yet Principle I forbids a serialization dependency in Domain. The
  neutral model keeps all validation rules in Domain and testable without JSON.
- **Alternatives**: (a) pass `JsonElement` into Domain, which violates Principle I; (b) validate in
  Infrastructure, which violates Principle I (validation must live in Domain/Application);
  (c) deserialize to a typed DTO, which loses "string vs number" and "missing vs null".
- **Verify**: how System.Text.Json reports `1e999` (non-finite after conversion) and `-0`; the source
  maps any number whose `double` is not finite to `Kind=Number, Number=±Infinity` so the validator
  returns `NonFiniteValue`. Literal `NaN`/`Infinity` tokens are not valid JSON and become
  `MalformedJson`; the string `"NaN"` is `InvalidType`.

## R2. Reason-code precedence

A line can have several defects. The validator checks in a fixed order and reports the first:
`MalformedJson` (also not-an-object) → `MissingField` (absent, null, or an empty/whitespace
deviceId, since an empty identifier is an absent identifier; checked in field order deviceId,
metric, ts, value, seq) → `InvalidType` (e.g. string `value`, numeric `deviceId`) →
`UnknownMetric` → `InvalidTimestamp` → `NonFiniteValue` → `InvalidSeq` (negative, a number with a
fraction or exponent such as `5.0`, or outside the 64-bit signed range).
Rationale: one stable reason per line keeps `lines = blank + parsed + invalid` exact, and the
breakdown sums to the invalid count. The order is documented in the README and tested.

## R3. Timestamp parsing and storage

- **Decision (parse)**: accept only strings matching the ISO-8601 date-time shape that *ends* in
  `Z` or `±hh:mm`, with optional fractional seconds; parse with
  `DateTimeOffset.TryParseExact` against explicit formats and convert to UTC. No offset → reject.
- **Decision (store)**: fixed-width ISO-8601 UTC text `yyyy-MM-ddTHH:mm:ss.fffffffZ` (7 fractional
  digits = .NET tick precision).
- **Rationale**: Fixed width makes lexicographic order equal chronological order, so range queries
  and the `(device_id, metric, ts_utc)` index work, text is readable in the walkthrough, and no
  precision is lost. Epoch milliseconds would silently merge timestamps that differ below 1 ms and
  therefore corrupt the dedup identity.
- **Alternatives**: `INTEGER` ticks (exact and sortable but unreadable); `TEXT` with variable
  precision (not sortable).

## R4. Storage layout and idempotent writes

- **Decision**: three tables exactly as in the input (`readings`, `rule_results`, `alerts`) with the
  natural keys as primary keys. `readings` carries a `classification` column
  (`'A'`/`'U'`) so aggregation is one indexed query.
- **Insert semantics**: `INSERT ... ON CONFLICT DO NOTHING` for readings; `rows affected = 0` ⇒
  AlreadyStored. This is the database-enforced half of Principle VI (no check-then-insert).
- **Re-evaluation semantics**: for each affected series, within the run's transaction:
  `DELETE` its `rule_results` and `alerts`, then `INSERT` the freshly computed ones, and `UPDATE`
  `readings.classification`. Because evaluation is a pure function of the stored readings and the
  rule set, a re-run produces byte-identical rows. Delete-then-insert (rather than per-row upsert)
  is chosen because it also removes results that no longer hold (rule edited, earlier readings
  arrived), which is the spec's "no orphan results or alerts" requirement (clarification 17).
  The primary keys still make a duplicate insert impossible.
- **Alternatives**: pure upsert per row (leaves orphans); `INSERT OR REPLACE` (deletes and
  re-inserts silently, hiding conflicts).
- **Affected series**: every series that has at least one parsed, non-duplicate-in-file reading in
  this file, regardless of whether it was newly or already stored. Simple, deterministic, and
  makes a re-run recompute and verify everything.

## R5. Transaction and Unit of Work

- **Decision**: one `IUnitOfWork.ExecuteAsync(Func<CancellationToken, Task>)` port. Infrastructure
  opens a SQLite connection and transaction, makes it ambient to the stores through a scoped
  `SqliteSession`, commits on success and rolls back on exception.
- **Rationale**: the whole run is all-or-nothing, so a crash mid-run leaves no half-evaluated
  series. A single writer connection suits SQLite.
- **Alternatives**: transaction per stage (partial states visible); passing `DbTransaction` through
  ports (leaks infrastructure types inward).

## R6. Startup sequencing

- **Decision**: `Program.cs` builds the app, runs `InitializeSchema`, `RuleCatalog` load/validate
  and `ProcessReadingsFile` explicitly, **then** calls `app.RunAsync()`.
- **Rationale**: hosted services may start after the web server begins listening, which would let
  the API answer before ingestion finishes. Explicit sequencing makes "ingest, report, then serve"
  (clarification 33) true by construction and works unchanged under `WebApplicationFactory`.
- **Alternatives**: `IHostedService` (ordering relative to Kestrel is subtle); background task
  (API could serve partial data).

## R7. Report counters and what the classification counts cover

- Counters are accumulated in a mutable `ProcessingReportBuilder` inside the use case and frozen
  into the immutable `ProcessingReport` record.
- **Decision**: `acceptable` and `unacceptable` count the *distinct readings of this file*
  (`newlyStored + alreadyStored`), so the invariant `newlyStored + alreadyStored = acceptable +
  unacceptable` (the spec's wording after clarification) is exact
  even when a series also holds readings from earlier files. `ruleEvaluations`, `ruleViolations`,
  `alertsGenerated` and `alertsSuppressed` count everything evaluated in the run (all stored
  readings of affected series). For the single-file case the two scopes coincide.
- **Rationale**: the spec's invariants are all about the file's lines; evaluation work is honestly
  reported as the work actually done. The README states this.
- **Flag**: this resolves an ambiguity the spec did not cover (multi-file runs); it can be revisited
  with `/speckit-clarify` but does not affect the supplied single-file scenario.

## R8. SustainedAbove scan (batch)

Single pass over the ordered series: state = `Idle | InEpisode(startTs, peak, coveredReadings)`.
- value > threshold: if Idle → start (startTs = this ts); update peak; add to covered.
- value <= threshold while InEpisode: close with endTs = this ts; `sustained = endTs − startTs >=
  duration`; the ending reading is not covered.
- end of data while InEpisode: close with endTs = last reading's ts and `openAtEnd = true`.
- A single above reading at the very end of data gives duration 0, so it is sustained only if
  `durationSeconds <= 0`, which startup validation forbids; no special case needed.
- Covered readings of sustained episodes are returned so the evaluator can mark them
  unacceptable; non-sustained episodes return nothing.
Complexity O(n) after the O(n log n) sort. The operator returns *episodes*; alerting is a separate
domain service so the operator stays free of cooldown policy (SRP, Principle V).

## R9. Alert policy

Episodes of one `(ruleId, deviceId, metric)` are processed in startTs order. An episode raises an
alert iff there is no previously *raised* alert for that key with
`episode.startTs − previous.startTs < cooldown` (strict: exactly equal to the cooldown raises).
The anchor moves only when an alert is raised, never for a suppressed episode, so a long chain of
short-gapped episodes yields an alert every `cooldown`, not one forever. Suppressed episodes are
returned so the Application layer can log and count them.

## R10. Operator extension model

- `IStatelessOperator` and `IStatefulOperator` are two small interfaces; both expose `Name` and a
  `Validate(RuleParameters)` returning a typed parameter object (or errors naming the rule).
- `OperatorRegistry` takes `IEnumerable<IStatelessOperator>` and `IEnumerable<IStatefulOperator>`
  from DI, builds case-insensitive dictionaries and rejects duplicate names at startup.
- Adding an operator = one class + one `AddSingleton<I…Operator, …>()` line; `SeriesEvaluator` and
  `RuleCatalog` are untouched (Open/Closed, Principle IV). A test registers a fake operator
  without editing any production class to prove this.
- Parameters: `RuleParameters` is a read-only dictionary of numbers
  (`value`, `min`, `max`, `threshold`, `durationSeconds`) populated by the JSON loader from
  top-level properties. Each operator validates the keys it needs. This keeps `rules.json` flat
  (clarification 18) without Domain knowing JSON.

## R11. Aggregation

- Application validates the query and returns a typed `AggregationResult` =
  `Ok(buckets) | Invalid(error[])`; the endpoint maps `Invalid` to `ProblemDetails` 400.
- `bucketCount = ceil((to − from) / bucketSeconds)` checked against `MaxAggregationBuckets`
  *before* any query runs. Bucket index = `floor((ts − from) / bucketSeconds)`.
- `IReadingQuery.GetAcceptableAsync(SeriesKey, from, to)` returns readings with
  `from <= ts < to` and classification `A`; `AggregationCalculator` is pure over them.
- Averages are returned as unrounded `double`; empty buckets have `count = 0` and null stats.

## R12. Logging

Application logs through `ILogger<T>` with message templates and named placeholders
(`{LineNumber}`, `{Reason}`, `{RuleId}`, …). Domain returns results (`Rejection`,
`ConflictingDuplicate`, `Episode`, `SuppressedEpisode`) and never logs. Per-reading Debug logs are
guarded with `logger.IsEnabled(LogLevel.Debug)` to avoid allocation on the hot path.

## R13. Package set (kept minimal)

- Domain: none. Application: `Microsoft.Extensions.Logging.Abstractions`,
  `Microsoft.Extensions.Options`. Infrastructure: `Microsoft.Data.Sqlite`, `Microsoft.Extensions.*`
  abstractions. Api: framework only.
- Tests: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Shouldly`,
  `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`.
  No NetArchTest: the architecture test reads `Assembly.GetReferencedAssemblies()` and the project
  file, which is a dozen lines and explainable in a walkthrough.
- Excluded by the input: ORM, MediatR, AutoMapper, generic repository, broker, auth, UI.

## R14. Time source

`TimeProvider` is injected only where elapsed run time is reported
(`GetTimestamp`/`GetElapsedTime` in the use case). Domain evaluation uses event time only and takes
no clock at all, so Principle III holds by construction. Tests use `FakeTimeProvider`.

## R16. Duplicate resolution (supersedes "first-wins")

- **Decision (in file)**: group kept candidates by `ReadingKey`. One copy: kept. Several copies with
  the same value: one kept, the rest counted as duplicates. Several copies with different values
  (conflicting duplicate): keep the **lowest** value (`double.CompareTo`), count the others as
  duplicates, report a `ConflictingDuplicate(key, keptValue, ignoredValues)` so Application logs a
  Warning. `Deduplicator` therefore needs no notion of file order, and `Kept` is returned sorted by
  `(Timestamp, Seq)` per series for stable output.
- **Rationale**: the constitution (Principle III) and SC-002 require identical results for any file
  order; file-order first-wins cannot give that when copies disagree. Lowest-value is arbitrary but
  deterministic, trivially explainable, and documented in the README.
- **Decision (across runs)**: stored value wins. `IReadingStore.InsertIfAbsent` returns
  `Inserted | AlreadyStored | AlreadyStoredConflicting`: `INSERT ... ON CONFLICT DO NOTHING`; when
  0 rows change, a primary-key `SELECT value` distinguishes the two already-stored outcomes.
  Both count as AlreadyStored; the conflicting one logs a Warning. Pure insert-if-absent is kept
  (Principle VI), at the cost that cross-run conflict outcomes depend on which file ran first
  (documented limitation).
- **Alternatives**: file-order first-wins (violates III); reject both copies (drops data and adds a
  reason code); lowest-value across runs (needs an UPDATE path, breaks insert-if-absent).

## R15. Seed data

The task's reading file was renamed from `readings.jsonl` to `sensor-data.json`. It is present at
`docs/sensor-data.json` (kept as the original given material); inspection shows 2,150 lines, each a
JSON object, so the content is JSON Lines despite the `.json` extension (devices PUMP-01, PUMP-02,
FAN-03, COMP-01; timestamps from 2025-06-01). The reader is format-based (line by line), never
extension-based, and a whole-file JSON array would yield `MalformedJson` per line. `rules.json` is
**not** shipped with the task and the user has none, so a seed `data/rules.json` was authored for this
repository (11 rules, all seven operators, one disabled, a device-specific and several all-device
rules) and `data/sensor-data.json` was copied from `docs/` (the original stays untouched). Default
`InputFilePath` is `data/sensor-data.json`, `RulesFilePath` is `data/rules.json`. The README must say
that the rules are the author's choice, not supplied material.

**Reference expectations** (independent Python simulation of the spec; use to cross-check T074, not
as the test oracle itself): 2,150 lines, 0 blank; 9 invalid (5 MissingField, 1 MalformedJson,
1 InvalidType for the string `"NaN"`, 2 InvalidTimestamp: `2025-06-31...` and a timestamp with no
offset); 2,141 parsed; 38 file duplicates (8 of them conflicting values); 2,103 kept; 5 alerts
(R10 FAN-03 vibration 08:26:50-08:28:10, R08 COMP-01 temperature 08:11:50-08:13:50, R09 PUMP-02
pressure 08:18:40-08:29:40, R08 FAN-03 temperature 08:28:10-08:29:40, R08 PUMP-02 temperature
08:22:30-08:24:10) and 4 episodes suppressed by the 5-minute cooldown; the data also contains
implausible but valid values (`1000000` temperature, `-9999` vibration) that the rules classify as
unacceptable, and readings are 10 s apart per series from 08:00:00Z to 08:34:50Z.
If the final numbers differ, find out which side is wrong before changing either.
Test fixtures are small, hand-written and independent of the supplied files, except for the single
end-to-end test that processes the supplied file and asserts the report invariants.
