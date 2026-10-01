# Data Model: SensorGuard

Types below are logical; C# shapes are records/enums in `SensorGuard.Domain` unless noted. No type
in Domain references JSON, SQLite, ASP.NET or logging. Rules and clarifications referenced as
`FR-nnn` / `Cn` (spec Clarifications, in order).

## Value objects (Domain)

| Type | Shape | Rules |
|------|-------|-------|
| `DeviceId` | `readonly record struct (string Value)` | Non-empty, non-whitespace after validation; compared ordinal, case-sensitive |
| `Metric` | `enum { Temperature, Pressure, Vibration }` | Parsed from lowercase `temperature`/`pressure`/`vibration` only (case-sensitive) |
| `ReadingKey` | `record (DeviceId, Metric, DateTimeOffset Timestamp, long Seq)` | Identity of a reading; `Timestamp` normalized to UTC (offset zero); `Seq >= 0` |
| `Reading` | `record (ReadingKey Key, double Value)` | `Value` finite |
| `SeriesKey` | `record (DeviceId, Metric)` | Grouping and state key; `Reading.Series` derived from `Key` |
| `RuleId` | `readonly record struct (string Value)` | Unique across the rule set |

Ordering of readings in a series: `(Timestamp, Seq)` ascending (C11). Two readings with the same
`Timestamp` and different `Seq` are distinct (spec edge case).

## Ingestion models

| Type | Layer | Fields |
|------|-------|--------|
| `Field` | Domain | `Kind (Missing/Null/String/Number/Other)`, `Text?`, `Number?`, `IsIntegerToken` |
| `RawReading` | Domain | `LineNumber`, `Field DeviceId, Metric, Ts, Value, Seq` |
| `RawLine` | Application port | `LineNumber`, `RawReading? Parsed`, `bool IsBlank`, `RejectionReason? ParseFailure` (a blank line is `IsBlank`; bad JSON is `ParseFailure = MalformedJson`) |
| `RejectionReason` | Domain | `MalformedJson, MissingField, InvalidType, UnknownMetric, InvalidTimestamp, InvalidSeq, NonFiniteValue` (C2) |
| `Rejection` | Domain | `LineNumber`, `RejectionReason Reason`, `string Detail` (field name or message) |
| `ValidationResult` | Domain | `Reading` **or** `Rejection` |
| `DedupResult` | Domain | `Kept` list (lowest value wins among conflicting copies, order-independent), `Duplicates` count, `ConflictingDuplicates` list of `(ReadingKey, keptValue, ignoredValues)` |
| `InsertOutcome` | Application port | `Inserted`, `AlreadyStored`, `AlreadyStoredConflicting` (stored value kept) |

State transitions: a line is exactly one of `Blank`, `Rejected(reason)`, `Parsed`; a parsed reading
is exactly one of `FileDuplicate`, `NewlyStored`, `AlreadyStored` (this yields the report
identities in `ProcessingReport`).

## Rules

| Type | Fields / behavior |
|------|-------------------|
| `RuleDefinition` (Application/Domain input) | `Id, Name, Enabled, MetricText, DeviceIdText?, OperatorName, RuleParameters` as read from data |
| `Rule` | `RuleId Id, string Name, bool Enabled, Metric Metric, DeviceId? DeviceId, string OperatorName, ValidatedParameters Parameters, OperatorKind Kind (Stateless/Stateful)`; built only by `RuleCatalog` |
| `Rule.AppliesTo(SeriesKey)` | `Enabled && Metric == series.Metric && (DeviceId is null || DeviceId == series.Device)` (FR-007) |
| `RuleParameters` | read-only map `string → double` (`value, min, max, threshold, durationSeconds`) |

Operator contracts:

```text
IStatelessOperator : Name; Validate(RuleParameters) -> ValidatedParameters | errors;
                     IsViolated(double value, Rule rule) -> bool;  Describe(double value, Rule rule) -> string
IStatefulOperator  : Name; Validate(RuleParameters) -> ValidatedParameters | errors;
                     Evaluate(Rule rule, IReadOnlyList<Reading> orderedSeries) -> IReadOnlyList<Episode>
```

Violation semantics (C22/C21): GreaterThan `v > value`; GreaterThanOrEqual `v >= value`;
LessThan `v < value`; LessThanOrEqual `v <= value`; Equal `v == value` (exact);
**Between** `v < min || v > max` (acceptable range inclusive). SustainedAbove violation = reading
covered by a sustained episode.

Validation (`RuleCatalog`, fail fast, errors name the rule id/name): duplicate id; unknown operator;
metric not in the enum; missing/non-finite parameter required by the operator; `min > max`;
`durationSeconds <= 0`. A rule whose device has no data is valid.

## Evaluation outputs

| Type | Fields |
|------|--------|
| `Episode` | `SeriesKey Series, DateTimeOffset StartTs, DateTimeOffset EndTs, double Peak, bool OpenAtEnd, IReadOnlyList<ReadingKey> Covered, bool Sustained` (only sustained episodes are returned) |
| `RuleViolation` | `ReadingKey Reading, RuleId Rule, string Reason` — key = `Reading` + `Rule` |
| `Classification` | `enum { Acceptable, Unacceptable }` |
| `ReadingClassification` | `ReadingKey, Classification, IReadOnlyList<RuleViolation> Violations` (Unacceptable ⇔ `Violations` non-empty) |
| `Alert` | `RuleId Rule, string RuleName, SeriesKey Series, StartTs, EndTs, Peak, OpenAtEndOfData` — identity `(Rule, Device, Metric, StartTs)` (C14) |
| `AlertDecision` | `Raised` alerts, `Suppressed` episodes (with the alert that suppressed them) |
| `SeriesEvaluation` | `ReadingClassification[]`, `Alert[]`, `SuppressedEpisode[]`, `int RuleEvaluations` |

Reason strings are human-readable, e.g. `temperature GreaterThan 100 fires at 103.2`,
`vibration LessThan 0 fires at -0.629`, `above 80 for 30s+ (episode started 2025-06-01T08:30:00Z)`.

## Aggregation

| Type | Fields |
|------|--------|
| `AggregationQuery` (Application) | `string? DeviceId, string? Metric, string? From, string? To, long? BucketSeconds` as received |
| `ValidatedAggregationQuery` | `SeriesKey, DateTimeOffset From, To, TimeSpan Bucket` |
| `AggregationBucket` | `DateTimeOffset Start, int Count, double? Average, double? Min, double? Max` (null iff `Count == 0`) |
| `AggregationResult` | `Ok(IReadOnlyList<AggregationBucket>)` or `Invalid(IReadOnlyList<string> Errors)` |

Bucket *i* starts at `From + i × Bucket`; `bucketCount = ceil((To − From)/Bucket)`, must be
`<= MaxAggregationBuckets` (10,000). Reading belongs to bucket `floor((ts − From)/Bucket)` when
`From <= ts < To`.

## ProcessingReport

```text
LinesRead, BlankLines, Parsed, InvalidRejected, InvalidByReason{reason→count},
FileDuplicates, AlreadyStored, NewlyStored,
RulesLoadedTotal, RulesLoadedEnabled, RuleEvaluations, Acceptable, Unacceptable,
RuleViolations, AlertsGenerated, AlertsSuppressed, Elapsed
```

Invariant methods (asserted by tests, Principle IX):

- `LinesRead == BlankLines + Parsed + InvalidRejected`
- `InvalidRejected == InvalidByReason.Values.Sum()`
- `Parsed == NewlyStored + AlreadyStored + FileDuplicates`
- `NewlyStored + AlreadyStored == Acceptable + Unacceptable` (see research R7)
- `RuleViolations >= Unacceptable` (each unacceptable reading has at least one violation); asserted
  for the single-file case only, because multi-file runs count violations over all evaluated
  readings (research R7).

## Persistence schema (Infrastructure, SQLite)

```text
readings(device_id TEXT, metric TEXT, ts_utc TEXT, seq INTEGER, value REAL, classification TEXT,
         PRIMARY KEY (device_id, metric, ts_utc, seq))  WITHOUT ROWID
rule_results(device_id TEXT, metric TEXT, ts_utc TEXT, seq INTEGER, rule_id TEXT, reason TEXT,
         PRIMARY KEY (device_id, metric, ts_utc, seq, rule_id),
         FOREIGN KEY (device_id, metric, ts_utc, seq) REFERENCES readings)
alerts(rule_id TEXT, device_id TEXT, metric TEXT, start_ts_utc TEXT, end_ts_utc TEXT,
         peak_value REAL, open_at_end INTEGER, rule_name TEXT,
         PRIMARY KEY (rule_id, device_id, metric, start_ts_utc))
INDEX ix_readings_series_ts ON readings(device_id, metric, ts_utc)   -- implied by the PK order
```

- `ts_utc` is fixed-width `yyyy-MM-ddTHH:mm:ss.fffffffZ` (research R3); `metric` is the lowercase
  name; `classification` is `'A'` or `'U'`.
- Because the readings primary key already starts with `(device_id, metric, ts_utc)`, the explicit
  index is only needed if the PK is reordered; the plan keeps the PK order and records that no
  extra index is required, avoiding a redundant structure. (If profiling disagrees, add it.)
- Idempotency keys (Principle VI): reading = PK of `readings`; rule result = PK of `rule_results`;
  alert = PK of `alerts`.

## Relationships

`Reading 1—* RuleViolation` (violations only; satisfied rules are not stored);
`Rule 1—* RuleViolation`; `Rule 1—* Alert`; `SeriesKey 1—* Reading`;
`Alert` covers a contiguous run of readings of one series (not stored as a relation; recomputable).
