# Quickstart & Validation Guide: SensorGuard

Runnable scenarios that prove the feature end to end. Types and rules are in
[data-model.md](data-model.md); the HTTP contract is [contracts/openapi.yaml](contracts/openapi.yaml);
the rules file contract is [contracts/rules.schema.json](contracts/rules.schema.json).
All outputs below were produced by running the application on the supplied data.

## Prerequisites

- .NET 10 SDK (`dotnet --version` reports 10.x)
- `data/sensor-data.json` (copied from `docs/`) and `data/rules.json` (authored seed rules, see research R15) in the repository
- No database setup: the SQLite file is created at `DatabasePath` on first run
- `curl` for the HTTP scenarios (any HTTP client works)

## Build and test

```bash
dotnet build SensorGuard.slnx -warnaserror
dotnet test SensorGuard.slnx
```

Expected: build with zero warnings; all four test projects green (298 tests), including the architecture test, the idempotent
re-run tests, and the end-to-end tests on the supplied file.

## Scenario 1: first run prints a reconciled report (US1, US6, SC-001)

```bash
dotnet run --project src/SensorGuard.Api -- --SensorGuard:DatabasePath=./sensorguard.db
```

Expected: warnings for the 9 rejected lines (with line numbers and reason) and for the 8 conflicting duplicates, Information lines for
each sustained episode, raised alert and suppressed alert, then the report, all before the API starts listening:

```
Total lines read                  2150        Newly stored readings             2103
Parsed readings                   2141        Rules loaded (total) / (enabled)  11 / 10
Invalid records rejected          9           Rule evaluations performed        3371
    MalformedJson 1, MissingField 5,          Acceptable readings               1812
    InvalidType 1, InvalidTimestamp 2         Unacceptable readings             291
Duplicates removed (in file)      38          Rule violations                   292
Already stored (earlier runs)     0           Alerts generated / suppressed     5 / 4
```

Check by hand: `2150 = 0 + 2141 + 9`; `2141 = 2103 + 0 + 38`; `2103 = 1812 + 291`.

## Scenario 2: re-run is idempotent (US4, SC-003)

Stop the app and run the same command again with the same database.

Expected: `Newly stored readings 0` and `Already stored (earlier runs) 2103`; acceptable, unacceptable, violations and alerts counts are
identical to scenario 1. The stored data is unchanged: `curl http://localhost:5000/api/alerts` returns the same 5 alerts, and
`curl http://localhost:5000/api/readings/unacceptable` the same 291 readings.

## Scenario 3: sustained episodes yield one alert each (US3, SC-004)

```bash
curl http://localhost:5000/api/alerts
```

Expected: five alerts, one per sustained episode outside cooldown: COMP-01 temperature 08:11:50 to 08:13:50, FAN-03 temperature
08:28:10 to 08:29:40, FAN-03 vibration 08:26:50 to 08:28:10, PUMP-02 pressure 08:18:40 to 08:29:40, PUMP-02 temperature 08:22:30
to 08:24:10 (peak values 71.789, 71.414, 6.278, 11.088, 71.542). Four further episodes appear only in the log and the report
(`Alert suppressed by cooldown`). A shuffled copy of the input gives identical results (SC-002; covered by an automated test).

## Scenario 4: aggregation over acceptable data (US5, SC-005)

```bash
curl "http://localhost:5000/api/aggregations?deviceId=PUMP-01&metric=temperature&from=2025-06-01T08:00:00Z&to=2025-06-01T08:35:00Z&bucketSeconds=300"
```

Expected: 200 with 7 contiguous buckets; the first is `{"start":"2025-06-01T08:00:00Z","count":30,"average":68.8275,"min":67.808,"max":70.112}`.
Unacceptable readings are never counted (FAN-03 temperature has a reading of 1,000,000 that no bucket contains).

```bash
curl -i "http://localhost:5000/api/aggregations?deviceId=x&metric=temperature&from=2025-06-01T09:00:00Z&to=2025-06-01T08:00:00Z&bucketSeconds=300"
curl -i "http://localhost:5000/api/aggregations?deviceId=x&metric=bogus&from=2025-06-01T08:00:00Z&to=2025-06-01T09:00:00Z&bucketSeconds=300"
```

Expected: `400` with `application/problem+json` naming the offending parameter (`from`, `metric`). An unknown device with a valid
metric returns 200 with all buckets empty (`count: 0`, null statistics).

## Scenario 5: invalid rules fail fast (US2, FR-005)

Point the app at a rules file containing an unknown operator, a duplicate id, an unknown metric and `min > max`:

```bash
dotnet run --project src/SensorGuard.Api -- --SensorGuard:RulesFilePath=./badrules.json --SensorGuard:DatabasePath=./bad.db
```

Expected: exit code 2, no database file created, and a message naming every problem, for example:

```
Cannot start: Invalid rule definitions:
 - Rule 'R7' ("Weird rule"): unknown operator 'Nope'
 - Rule 'R1' ("Dup"): duplicate rule id 'R1'
 - Rule 'R1' ("Dup"): unknown metric 'humidity' (expected temperature, pressure or vibration)
 - Rule 'R1' ("Dup"): min must not be greater than max
```

## Scenario 6: add an operator without touching evaluation code (US2.9, FR-016)

Follow the three-step recipe in the README ("Adding a new operator"): add a class implementing `IStatelessOperator`, register it in the Api
composition root (`ServiceRegistration.cs`; Domain has no DI package), add a rule that uses it. Expected: the new rule evaluates;
`SeriesEvaluator` and `RuleCatalog` have no diff (proven by `OperatorRegistryTests`).
