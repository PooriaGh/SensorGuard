# SensorGuard

A small backend service that ingests numeric IoT sensor readings from a JSON Lines file, cleans messy data,
classifies readings against data-driven rules (including a stateful, time-based operator), raises one deduplicated
alert per sustained episode, and serves time-bucketed aggregates of **acceptable** data over a GET API.

C# on .NET 10, ASP.NET Core minimal API, SQLite. The project was built spec-first (see [Process](#process)); every
decision below is recorded in `specs/001-sensor-ingestion-alerting/` and can be traced to a requirement.

## Contents

- [How to run](#how-to-run) · [How to test](#how-to-test)
- [Architecture and trade-offs](#architecture-and-trade-offs)
- [Storage: SQLite](#storage-sqlite)
- [Data cleaning policy](#data-cleaning-policy)
- [Rule model and evaluation policy](#rule-model-and-evaluation-policy)
- [SustainedAbove: state and ordering strategy](#sustainedabove-state-and-ordering-strategy)
- [Alerts and cooldown](#alerts-and-cooldown)
- [Idempotency and keying](#idempotency-and-keying)
- [Aggregation API](#aggregation-api)
- [Processing report and logging](#processing-report-and-logging)
- [Adding a new operator](#adding-a-new-operator)
- [Known limitations](#known-limitations)
- [Process and AI usage disclosure](#process-and-ai-usage-disclosure)

## How to run

Prerequisite: the .NET 10 SDK (the constitution allows .NET 8 or 10; only 10 was available on the development machine).

```bash
dotnet run --project src/SensorGuard.Api
```

At startup the app, in this order: loads and validates `data/rules.json` (any problem aborts with exit code 2 before the
database is touched), creates the SQLite schema, ingests `data/sensor-data.json`, **prints the processing report**, and only
then starts serving the API (default `http://localhost:5000`). Re-running the process is the idempotent re-run.

Configuration (`appsettings.json`, section `SensorGuard`; any value can be overridden on the command line, e.g.
`--SensorGuard:DatabasePath=./other.db`):

| Option | Default | Meaning |
|---|---|---|
| `InputFilePath` | `data/sensor-data.json` | JSON Lines input (the `.json` extension is irrelevant; the reader is line-based) |
| `RulesFilePath` | `data/rules.json` | Rules seed file |
| `DatabasePath` | `sensorguard.db` | SQLite file, created on first run |
| `AlertCooldown` | `00:05:00` | Alert cooldown (see below) |
| `MaxAggregationBuckets` | `10000` | Upper bound on buckets one aggregation request may produce |

Relative input and rules paths are looked up in the working directory first, then next to the built application.

Endpoints:

```bash
# Time-bucketed statistics over acceptable readings, [from, to), buckets aligned to from
curl "http://localhost:5000/api/aggregations?deviceId=PUMP-02&metric=pressure&from=2025-06-01T08:00:00Z&to=2025-06-01T08:35:00Z&bucketSeconds=300"
# Read-only views that show the two separate outputs (acceptable / unacceptable) and the alerts
curl http://localhost:5000/api/alerts
curl "http://localhost:5000/api/readings/unacceptable?deviceId=FAN-03&metric=temperature"
```

The contract is in `specs/001-sensor-ingestion-alerting/contracts/openapi.yaml`.

### What the supplied data produces

On `data/sensor-data.json` with the seed rules (about 250 ms on a laptop):

```
Total lines read                  2150      Duplicates removed (in file)      38
Parsed readings                   2141      Newly stored readings             2103
Invalid records rejected          9         Rule evaluations performed        3371
    MalformedJson 1, MissingField 5,        Acceptable / Unacceptable         1812 / 291
    InvalidType 1, InvalidTimestamp 2       Rule violations                   292
Rules loaded (total / enabled)    11 / 10   Alerts generated / suppressed     5 / 4
```

A second run reports `Newly stored 0`, `Already stored 2103` and identical everything else. These numbers were first
computed by an independent script written from the spec and are asserted exactly by an automated end-to-end test.

## How to test

```bash
dotnet build SensorGuard.slnx -warnaserror
dotnet test SensorGuard.slnx
```

298 tests across four projects. Domain tests are pure and run in milliseconds (no storage, no I/O); Application tests use
in-memory stores and a fake `TimeProvider`; Infrastructure tests use temp files and an in-memory SQLite database; API tests
host the real application with `WebApplicationFactory`. Tests never use wall-clock time or unseeded randomness. Mandatory
coverage: deduplication, invalid-record rejection, every operator, rule applicability, SustainedAbove with shuffled data and
boundaries, alert cooldown, idempotent re-run, aggregation (bucket boundaries, acceptable-only, empty buckets), report counts
and their reconciliation, and an architecture test that fails if Domain references anything outside itself.

## Architecture and trade-offs

Clean Architecture; dependencies point inward only.

```
SensorGuard.Api            composition root, minimal API endpoints, startup sequence
SensorGuard.Infrastructure JSONL reader, rules.json loader, SQLite stores (hand-written SQL)
SensorGuard.Application    use cases (ProcessReadingsFile, GetAggregation), ports (interfaces), report formatting
SensorGuard.Domain         validation, dedup, rules, operators, series evaluation, alert policy, aggregation. No packages.
```

- Validation, deduplication, applicability, evaluation, sustained state, alerting and classification are all in Domain.
  Controllers/endpoints and the SQL stores contain none of it.
- Infrastructure is reached only through ports defined inward (`IReadingSource`, `IRuleSource`, `IReadingStore`, ...), so a
  new input source or storage engine is a new adapter. JSON never enters Domain: the JSONL adapter hands Domain a neutral
  view of each field (missing / null / string / number / other) so Domain can still reject "value sent as a string".
- Logging uses `ILogger` in Application only; Domain returns results (rejections, conflicts, episodes) and Application logs
  them. Time is injected (`TimeProvider`) and used only to report elapsed run time; evaluation uses event time and no clock.
- Deliberately not built: brokers, auth, UI, deployment, rule-management endpoints, an ORM, a generic repository, MediatR.

**Batch versus streaming.** The input is a finite file, so the pipeline is batch: read, validate, dedupe, then sort each
series and scan it. The result is fully deterministic whatever the file order. The alternative, incremental streaming with a
bounded lateness window, gives lower latency and memory but needs a watermark, and late data can change an already-emitted
result. At about 2,150 readings (and O(n) memory, O(n log n) time) batch is the simpler and more correct choice. The stateful
operator interface (`IStatefulOperator`: ordered series in, episodes out) does not preclude a streaming implementation later.

## Storage: SQLite

SQLite via `Microsoft.Data.Sqlite` with small hand-written SQL, no ORM. It is file based (zero setup for a reviewer), supports
unique constraints and `INSERT ... ON CONFLICT`, so idempotency is enforced by the database as well as by the domain, and it
runs in memory for tests. Timestamps are stored as fixed-width UTC text `yyyy-MM-ddTHH:mm:ss.fffffffZ` so text order equals
time order (index-friendly range queries) and no sub-millisecond precision is lost. The whole run is one transaction.
The cost: a single writer, which suits a single-run batch ingest.

## Data cleaning policy

Every line is untrusted; a bad line is rejected with a stable reason code, counted, logged at Warning, and processing continues.

| Reason | When |
|---|---|
| `MalformedJson` | not valid JSON, or valid JSON that is not an object |
| `MissingField` | deviceId, metric, ts, value or seq missing, `null`, or deviceId empty/whitespace |
| `InvalidType` | wrong JSON type, e.g. `value` as a string (even `"12"` or `"NaN"`) |
| `UnknownMetric` | metric not exactly `temperature`, `pressure` or `vibration` (case-sensitive) |
| `InvalidTimestamp` | not ISO-8601 with `Z` or an explicit offset (a timestamp **without** an offset is ambiguous and rejected); impossible dates such as June 31st |
| `NonFiniteValue` | NaN or Infinity, including numbers beyond the double range |
| `InvalidSeq` | negative, fractional (`5.0` too: only plain integer tokens count), or beyond the 64-bit range |

A line with several defects reports the first by that precedence, so the per-reason counts always sum to the invalid total.
Blank lines are skipped silently (counted in "lines read", not as invalid); a UTF-8 BOM and CRLF are tolerated.
**Plausibility is not validated**: a negative vibration or a temperature of 1,000,000 is well-formed data. Rules decide
acceptability. A rule violation is not malformed data, and invalid or duplicate readings are never counted as unacceptable.

**Duplicate policy.** Identity is the `(deviceId, metric, ts, seq)` quadruple. Identical copies collapse to one. If copies
carry *different* values, the **lowest value wins**, and a Warning is logged. This was chosen over "first-wins" deliberately:
first-wins depends on file order, which would make results depend on the order of the file (the file is not sorted, and the
design promise is that any order gives the same classification and alerts). Lowest-value is arbitrary but order-independent
and trivial to explain; the supplied file contains 8 such conflicts. Across runs the opposite rule applies: a reading already
stored keeps its stored value (a Warning is logged), because the idempotency design is pure insert-if-absent; see limitations.

## Rule model and evaluation policy

Rules are data in `rules.json`: `id`, `name`, `enabled`, `metric`, optional `deviceId`, `operator`, and the operator's
parameters as flat numeric properties (`value`; `min`/`max`; `threshold`/`durationSeconds`). Operator names match
case-insensitively. Adding, enabling, disabling or changing a rule is a data change. A rule applies to a metric, and
optionally to one device; without `deviceId` it applies to every device carrying that metric. Disabled rules are never evaluated.

**Every rule describes the VIOLATING condition**, except `Between`, which describes the acceptable range:

| Operator | Violation when |
|---|---|
| `GreaterThan` / `GreaterThanOrEqual` | `value >` / `>=` the rule's `value` |
| `LessThan` / `LessThanOrEqual` | `value <` / `<=` the rule's `value` |
| `Equal` | `value ==` the rule's `value` (exact numeric equality, no tolerance) |
| `Between` | `value < min` or `value > max` (min and max themselves are acceptable) |
| `SustainedAbove` | the metric stays strictly above `threshold` for at least `durationSeconds` of event time |

A reading is **acceptable** if no applicable rule is violated and **unacceptable** if at least one is; the result lists every
violated rule (the union of per-reading and sustained rules) with a human-readable reason such as
`temperature GreaterThan 100 fires at 103.2` or `above 80 for 30s+ (episode started 2025-06-01T08:00:20Z)`.
**If no enabled rule applies to a reading it is acceptable** (policy). Rules are validated at startup and fail fast, naming
every offending rule: duplicate id, unknown operator or metric, missing or non-finite parameter, `min > max`, `durationSeconds <= 0`.
A rule for a device that has no data is valid and simply never fires.

`data/rules.json` is **authored by this project**: the task shipped no rules file. It has 11 rules covering all seven
operators (one disabled, one that never fires on this data, overlapping `Between`/`GreaterThanOrEqual`/sustained rules on pressure). The
rule `LessThan 0` on vibration marks about 91 readings of `PUMP-01` as unacceptable, which is a content choice, not a bug.

## SustainedAbove: state and ordering strategy

Per `(deviceId, metric)` series the readings are sorted by `(ts, seq)` (ties on the timestamp are broken by `seq`, so the order
is total), then scanned once:

- A reading is "above" when `value > threshold` strictly; a value equal to the threshold is not above.
- An **episode starts** at the timestamp of the first above reading (after a non-above one, or at series start) and **ends**
  at the timestamp of the first later reading at or below the threshold. If the data ends first, it ends at the last observed
  reading and the alert is flagged `openAtEndOfData`.
- It is **sustained** when `end - start >= durationSeconds` (inclusive). A single above reading followed by a non-above one
  lasts the gap between them.
- **Gaps**: the last value is held across gaps; an episode is never split by missing data.
- **Reading-level policy**: every reading with a timestamp in `[start, end)` of a sustained episode is unacceptable (with the
  rule and the episode start in the reason). The ending reading is not part of the episode, and above-threshold runs shorter than
  the duration stay acceptable. A suppressed episode's readings are still unacceptable.
- **Late arrivals**: within a run there are none, since ordering is fully resolved before evaluation. Across runs, a later
  file with earlier-timestamped readings causes every affected series to be re-evaluated from all its stored readings, and its
  stored results and alerts are replaced; stale ones never linger.

## Alerts and cooldown

One sustained episode yields one alert, not one per offending reading: `ruleId`, `ruleName`, `deviceId`, `metric`, `startTs`,
`endTs`, `peakValue`, `openAtEndOfData`. **Cooldown** (default 5 minutes, configurable): per `(rule, deviceId, metric)`, a new
episode is suppressed if it **starts less than the cooldown after the start of the previous *raised* alert**; at exactly the
cooldown it raises. The anchor is the start of the last alert actually raised (never of a suppressed episode), so a chain of
closely spaced episodes alerts once per cooldown instead of never again. Different rules, devices and metrics are independent.
Suppressed episodes are logged at Information and counted in the report. (Anchoring on the *start* rather than the end is
simple and stays stable for long episodes.)

## Idempotency and keying

Processing the same input twice creates no duplicate readings, rule results or alerts. The keys are deterministic natural
keys, enforced by primary keys and `INSERT ... ON CONFLICT`, not by check-then-insert:

| Data | Key | Write rule |
|---|---|---|
| Reading | `(deviceId, metric, ts_utc, seq)` | `INSERT ... ON CONFLICT DO NOTHING`; a conflict counts as *already stored* |
| Rule result (violation) | reading key + `ruleId` | rows of a re-evaluated series are replaced inside the run's transaction |
| Alert | `(ruleId, deviceId, metric, startTs)` | rows of a re-evaluated series are replaced inside the run's transaction |

Evaluation always runs over *all stored readings* of the affected series, so a re-run recomputes and replaces rather than
appends, and results that no longer hold (a rule was edited, earlier readings arrived) disappear. This is proven by automated
re-run tests at the Application level (fakes), the SQLite level, and end to end on the supplied file.

## Aggregation API

`GET /api/aggregations?deviceId=&metric=&from=&to=&bucketSeconds=` returns `{deviceId, metric, from, to, bucketSeconds,
buckets:[{start, count, average, min, max}]}` over **acceptable readings only**, within the half-open interval `[from, to)`.
Buckets are aligned to `from` (bucket *i* starts at `from + i × bucketSeconds`), so a reading exactly on a boundary belongs to the
bucket starting at that instant; the last bucket may extend past `to`, but readings at or after `to` are excluded. **Empty buckets
are reported** with `count: 0` and `null` average/min/max, so a chart gets a continuous series. Averages are unrounded doubles.
An unknown device with a valid metric returns 200 with all-empty buckets. Missing or unparseable parameters, `from >= to`,
`bucketSeconds <= 0`, an unknown metric, or more than `MaxAggregationBuckets` buckets return a `400` problem-details body naming the parameter.

## Processing report and logging

At the end of ingestion the report is printed and logged (before the API starts): lines read, blank lines, parsed, invalid
(with the per-reason breakdown), file duplicates, already stored, newly stored, rules loaded, rule evaluations, acceptable,
unacceptable, rule violations, alerts generated and alerts suppressed. The numbers reconcile, and tests assert it:
`lines = blank + parsed + invalid`; `parsed = newly stored + already stored + file duplicates`;
`newly + already stored = acceptable + unacceptable` (these two count *this file's* distinct readings); a violated invariant
is logged as a Warning. Levels: Warning for rejected lines and conflicting duplicates; Information for rules loaded, sustained
episodes, raised and suppressed alerts and the report; Debug for per-reading detail (nothing per reading at Information).

## Adding a new operator

Three steps, with no change to the evaluator, the catalog or the registry:

1. Add a class implementing `IStatelessOperator` (per-reading) or `IStatefulOperator` (per-series), e.g.:

   ```csharp
   public sealed class NotEqualOperator : SingleValueOperator          // src/SensorGuard.Domain/Rules/Operators/
   {
       public override string Name => "NotEqual";
       protected override bool Fires(double reading, double limit) => reading != limit;
   }
   ```

   (`SingleValueOperator` is the shared base for comparison operators; a custom operator implements `Name`, `Validate`,
   `IsViolated` and `Describe` directly.)
2. Register it with one line in `src/SensorGuard.Api/ServiceRegistration.cs`:
   `services.AddSingleton<IStatelessOperator, NotEqualOperator>();`
3. Use it in `rules.json`: `{ "id": "R12", ..., "operator": "NotEqual", "value": 0 }`. New parameters need no loader change:
   any extra numeric property becomes an operator parameter.

`OperatorRegistryTests` proves this by registering an operator defined inside the test, with no production class edited.

## Known limitations

- **Cross-run conflicting duplicates** are order-dependent: if the same reading key arrives in two different files with
  different values, the first file processed wins (pure insert-if-absent). Within one run the result never depends on file order.
  The plan records this as a justified exception to determinism for the sake of simple idempotency.
- Evaluation loads every stored reading of the affected series into memory (fine at this scale; the streaming-with-watermark
  alternative is described above).
- Single-run batch ingestion at startup; no continuous ingestion, brokers or authentication (out of scope by design).
- The report's acceptable/unacceptable counts cover the current file's distinct readings, while rule-evaluation, violation and alert
  counts cover everything evaluated in the run (identical for a single file).
- Timestamps without an offset, `seq` written as `5.0`, and non-object lines are rejected rather than guessed at.

## Process and AI usage disclosure

Built with GitHub Spec Kit: `.specify/memory/constitution.md` (principles and governance), then `specs/001-sensor-ingestion-alerting/`
(`spec.md` with its clarifications, `plan.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`, `tasks.md`), then
implementation task by task, test first, one commit per slice (see `git log`).

**AI disclosure:** AI tools were used throughout. The code, tests and documentation were written with Claude Code (Claude
Sonnet 5.5) under the author's direction; the policy decisions (duplicate policy, cooldown anchor, rule semantics, empty buckets,
timestamp handling and so on) were taken by the author in the spec clarifications. The constitution requires that every
generated line is understood and owned by the author, who is expected to be able to explain any part of it.
