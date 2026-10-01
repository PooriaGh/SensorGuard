# Quickstart & Validation Guide: SensorGuard

Runnable scenarios that prove the feature end to end. Types and rules are in
[data-model.md](data-model.md); the HTTP contract is [contracts/openapi.yaml](contracts/openapi.yaml);
the rules file contract is [contracts/rules.schema.json](contracts/rules.schema.json).

## Prerequisites

- .NET 10 SDK (`dotnet --version` reports 10.x)
- `data/sensor-data.json` (copied from `docs/`) and `data/rules.json` (authored seed rules, see research R15) in the repository
- No database setup: the SQLite file is created at `DatabasePath` on first run

## Build and test

```bash
dotnet build SensorGuard.slnx -warnaserror
dotnet test SensorGuard.slnx
```

Expected: build with zero warnings; all four test projects green, including the architecture test
and the idempotent re-run tests.

## Scenario 1: first run prints a reconciled report (US1, US6, SC-001)

```bash
dotnet run --project src/SensorGuard.Api -- --SensorGuard:DatabasePath=./sensorguard.db
```

Expected, before the API starts listening: a report listing lines read, blank lines, parsed,
invalid (with per-reason breakdown), file duplicates, already stored (0), newly stored, rules loaded,
rule evaluations, acceptable, unacceptable, rule violations, alerts generated, alerts suppressed.
Check by hand: `lines = blank + parsed + invalid`; `parsed = newly + already + duplicates`;
`newly + already = acceptable + unacceptable`.

## Scenario 2: re-run is idempotent (US4, SC-003)

Stop the app and run the same command again with the same database.

Expected: `newly stored = 0`, `already stored =` the first run's newly stored count, identical
acceptable/unacceptable/violation/alert counts, and unchanged row counts:

```bash
sqlite3 sensorguard.db "select count(*) from readings; select count(*) from rule_results; select count(*) from alerts;"
```

## Scenario 3: sustained episode yields one alert (US3, SC-004)

```bash
sqlite3 sensorguard.db "select rule_id, device_id, metric, start_ts_utc, end_ts_utc, peak_value, open_at_end from alerts order by start_ts_utc;"
```

Expected: one row per sustained episode outside cooldown; suppressed episodes appear only in the
report and as Information log lines. Shuffling `sensor-data.json` and re-running against a fresh
database yields identical rows (SC-002; also covered by an automated test).

## Scenario 4: aggregation over acceptable data (US5, SC-005)

```bash
curl "http://localhost:5000/api/aggregations?deviceId=PUMP-01&metric=temperature&from=2025-06-01T08:00:00Z&to=2025-06-01T09:00:00Z&bucketSeconds=300"
```

Expected: 200 with 12 contiguous buckets; empty buckets show `count: 0` and null statistics;
unacceptable readings are not counted.

```bash
curl -i "http://localhost:5000/api/aggregations?deviceId=x&metric=temperature&from=2025-06-01T09:00:00Z&to=2025-06-01T08:00:00Z&bucketSeconds=300"
curl -i "http://localhost:5000/api/aggregations?deviceId=x&metric=bogus&from=2025-06-01T08:00:00Z&to=2025-06-01T09:00:00Z&bucketSeconds=300"
```

Expected: `400` with a `ProblemDetails` body naming the offending parameter. An unknown device with a
valid metric returns 200 with all buckets empty.

## Scenario 5: invalid rules fail fast (US2, FR-005)

Edit a copy of `rules.json` to contain `"operator": "Nope"` and start with
`--SensorGuard:RulesFilePath=<copy>`.

Expected: process exits non-zero before touching the database, with a message naming the offending
rule id and the unknown operator.

## Scenario 6: add an operator without touching evaluation code (US2.9, FR-016)

Follow the three-step recipe in the README ("Adding an operator"): add a class implementing
`IStatelessOperator`, register it in the Api composition root (`Program.cs`; Domain has no DI package), add a rule that uses it. Expected: the
new rule evaluates; `SeriesEvaluator` and `RuleCatalog` have no diff (verified by the registry
extension test).
