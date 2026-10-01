# Feature Specification: Sensor Ingestion, Stateful Rule Evaluation & Alerting

**Feature Branch**: `001-sensor-ingestion-alerting`

**Created**: 2026-10-01

**Status**: Draft

**Input**: User description: "Sensor Ingestion, Stateful Rule Evaluation & Alerting (SensorGuard) — ingest a messy JSONL file of IoT sensor readings, clean it, classify readings against data-driven rules (including a sustained-condition rule), raise one deduplicated alert per sustained episode, support safe re-runs, and expose time-bucketed aggregates of acceptable data."

## Overview

Operators of industrial equipment (pumps, fans, compressors) receive streams of numeric sensor
readings that are messy: duplicated, out of order, and sometimes malformed. They need to trust
which readings are acceptable, be told once when a sustained dangerous condition occurs, and query
clean time-bucketed statistics. Without that, bad data pollutes statistics and every offending
reading triggers noise instead of one meaningful alert.

**Users**

- **Operations engineer**: wants a trustworthy processing report, alerts for sustained problems, and aggregates over acceptable data.
- **Rule administrator**: defines and toggles rules by editing a seed file, with no code changes.
- **Downstream consumer (API client)**: queries aggregated acceptable data per device and metric.

**Input**: `sensor-data.json` (the task's `readings.jsonl`, renamed; the content is still JSON
Lines, one JSON object per line, not a single JSON array). About 2,150 readings across several
devices and metrics. Each line
has deviceId (text), metric (temperature, pressure or vibration), ts (ISO-8601 timestamp),
value (number) and seq (integer). The file is not sorted by time. A rules seed file (rules.json)
is loaded at startup.

**Execution model**: The application processes the input file at startup (path configurable,
default `sensor-data.json` next to the rules file), prints the processing report, then serves the
aggregation API. Re-running the process is the idempotent re-run.

## Clarifications

### Session 2026-10-01

Data cleaning
- Q: Which duplicate wins? → A: Within one file, copies with identical value are collapsed (the first is kept, the rest counted as duplicates). Copies with DIFFERENT values (a conflicting duplicate) are resolved by a deterministic, order-independent tie-break: the copy with the lowest value is kept, the others are counted as duplicates, and a Warning is logged. Rationale: Principle III and SC-002 require identical results for any file order, which file-order first-wins cannot give. (Supersedes the earlier "first occurrence in file order" decision.)
- Q: If a reading already stored from an earlier run arrives again with a different value, which is kept? → A: The stored value wins (insert-if-absent); the reading counts as AlreadyStored and a Warning is logged for the conflict. Across runs the outcome therefore depends on which file was processed first; the README states this limitation.
- Q: What makes a line invalid? → A: Not valid JSON or not a JSON object; required field missing or null; wrong JSON type (value must be a JSON number, not a string); deviceId empty or whitespace; metric not in {temperature, pressure, vibration} (case-sensitive); ts not parseable as ISO-8601; seq not an integer or negative; value NaN or Infinity. Each rejection has a stable reason code (MalformedJson, MissingField, InvalidType, UnknownMetric, InvalidTimestamp, InvalidSeq, NonFiniteValue) and the report breaks rejected counts down by reason.
- Q: How is a whole-valued decimal seq such as 5.0 treated? → A: Rejected as InvalidSeq. Only plain integer tokens (no fraction, no exponent) within the 64-bit signed range are valid; a negative seq or one above that range is also InvalidSeq.
- Q: Which timestamps are accepted? → A: ISO-8601 with `Z` or an explicit offset, normalized to UTC; fractional seconds accepted; timestamps with no offset are rejected as ambiguous (InvalidTimestamp).
- Q: Is there physical-range validation? → A: No. Negative vibration (e.g. -0.629) is well-formed data; rules decide acceptability. Invalid means structurally or semantically unparseable only.
- Q: How are blank lines, BOM and line endings handled? → A: Blank lines are skipped silently, counted in lines read but not as invalid. A UTF-8 BOM and CRLF are tolerated.

Ordering and stateful evaluation
- Q: Ordering strategy? → A: Batch, sort then scan. Load all valid, de-duplicated readings, group by (deviceId, metric), sort by (ts, seq), then scan. Rationale: finite file, fully deterministic regardless of file order, trivial memory at ~2,150 readings. The README records the trade-off against streaming with a bounded lateness window (lower latency/memory, but needs a watermark and late data can change results) and notes the stateful evaluator interface does not preclude a future streaming implementation.
- Q: Late arrivals? → A: Within a run there are none, since ordering is resolved before evaluation. Across runs, a re-run or later file containing earlier-timestamped readings causes the affected (device, metric) series to be re-evaluated from stored readings. Documented in the README.
- Q: Sustained window definition? → A: A reading is "above" when value > threshold strictly. An episode starts at the ts of the first above reading after a non-above reading (or at series start) and ends at the ts of the first subsequent reading with value <= threshold (endTs), or at the last observed reading's ts if the data ends first. It is sustained when endTs − startTs >= durationSeconds (inclusive). A single above reading followed by a non-above reading has duration equal to the gap between them.
- Q: Gaps in data? → A: No maximum-gap handling; consecutive above readings are assumed to stay above in between (hold-last-value). Episodes are never split on a gap.
- Q: Which readings of a sustained episode are unacceptable? → A: Every reading with ts in [startTs, endTs) of a sustained episode, with the rule identified and a reason such as "above 80 for 30s+ (episode started 08:30:00Z)". The reading that ends the episode is not part of it. Readings in above-threshold runs shorter than the duration stay acceptable.
- Q: Same-timestamp ordering? → A: Ties on ts are broken by seq ascending (then deviceId and metric, implied by grouping), so ordering is total and deterministic.

Alerting
- Q: Cooldown anchor? → A: Measured from the previous alert's startTs for the same (ruleId, deviceId, metric). A new episode whose startTs is less than the cooldown (default 5 minutes, configurable) after the last alert's startTs is suppressed. A suppressed episode creates no alert but its readings are still unacceptable. Suppressed episodes are logged at Information.
- Q: Alert content? → A: ruleId, ruleName, deviceId, metric, startTs, endTs, peakValue, and OpenAtEndOfData (true when endTs is the last observed ts rather than a real drop-back).
- Q: Alert identity? → A: (ruleId, deviceId, metric, startTs).

Idempotency
- Q: Natural keys? → A: Reading = (deviceId, metric, ts, seq); rule result = reading key + ruleId; alert = (ruleId, deviceId, metric, startTs). Persisted with unique constraints plus insert-if-absent semantics.
- Q: Re-run report semantics? → A: "Duplicates removed" counts file-level duplicates only. Readings already in storage are counted as AlreadyStored, not as newly stored. Reconciliation: parsed = newly stored + already stored + file duplicates.
- Q: Re-evaluation on re-run? → A: Evaluation is recomputed from all stored readings of the affected series. Results are upserted by key; unchanged results do not count as new; no orphan results or alerts remain.

Rules
- Q: rules.json shape? → A: An array of objects with id, name, enabled, metric, optional deviceId, operator, and parameters at the top level: `value` (comparison operators and Equal); `min`, `max` (Between, inclusive); `threshold`, `durationSeconds` (SustainedAbove). Operator names match case-insensitively.
- Q: Startup validation? → A: Fail fast naming the offending rule on: duplicate rule id, unknown operator, missing or invalid parameter, min > max, durationSeconds <= 0, unknown metric. A rule referencing a device with no data is valid and never fires.
- Q: Equal semantics? → A: Exact numeric equality, no tolerance.
- Q: Rule semantics (violation vs acceptable condition)? → A: Every rule describes the BAD condition, except Between: GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Equal and SustainedAbove each FIRE (violate) when their stated condition is true (e.g. "temperature GreaterThan 100" fires above 100; "vibration LessThan 0" fires on negatives; Equal fires when value equals `value`). Between describes the ACCEPTABLE operating range, inclusive on both ends: it fires when value < min or value > max. Documented in the README; seed file and tests follow it.
- Q: Between: fires inside or outside [min, max]? → A: Outside (user-confirmed). Resolves the conflict between "every rule describes a violation" and "acceptable when min <= value <= max".
- Q: No applicable rule? → A: The reading is acceptable.
- Q: Sustained and per-reading rules together? → A: Evaluated independently; the reading's violated-rule list is the union.

Aggregation API
- Q: Route? → A: `GET /api/aggregations?deviceId=&metric=&from=&to=&bucketSeconds=`; from and to are ISO-8601 with an offset; interval is [from, to).
- Q: Bucket alignment? → A: Aligned to `from`: bucket i starts at from + i × bucketSeconds. The last bucket may extend past `to`, but readings at or after `to` are excluded.
- Q: Empty buckets? → A: Reported with count 0 and null average, min and max (continuous series).
- Q: Limits and errors? → A: 400 for missing parameters, unparseable timestamps, from >= to, bucketSeconds <= 0, unknown metric, or more than 10,000 buckets. An unknown device with a valid metric returns 200 with all-empty buckets.
- Q: Rounding? → A: Averages are returned unrounded as doubles.

Report and logging
- Q: Report contents? → A: Lines read (including blank), blank lines skipped, parsed, invalid rejected (per-reason breakdown), file duplicates removed, already stored, newly stored, rules loaded (enabled and total), rule evaluations performed (reading × applicable enabled rule, plus one per sustained rule and series evaluation), acceptable, unacceptable, rule violations (total violated rule results; one reading can contribute several), alerts generated, alerts suppressed by cooldown. Invariants: lines = blank + parsed + invalid; parsed = newly stored + already stored + file duplicates; newly stored + already stored = acceptable + unacceptable (acceptable/unacceptable count this file's distinct readings).
- Q: When does the report print? → A: At the end of ingestion, to the console and as a structured log entry; the API stays available afterwards.
- Q: Log levels? → A: Warning for invalid records and conflicting duplicates; Information for rules loaded, episodes detected, alerts raised, suppressed alerts and the report; Debug for per-reading detail.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Clean ingestion of messy data (Priority: P1)

As an operations engineer, I want the file ingested while duplicates and invalid lines are removed
and counted, so that only trustworthy readings are evaluated.

**Why this priority**: Every other capability depends on trustworthy input; bad data must never
reach classification or statistics.

**Independent Test**: Process a fixture file containing duplicates and each kind of invalid line;
verify kept readings, duplicate count, and per-reason rejection counts.

**Acceptance Scenarios**:

1. **Given** two lines with identical (deviceId, metric, ts, seq), **When** processed, **Then** exactly one reading is kept and the rest are counted as duplicates; if their values differ, the lowest value is kept regardless of file order and a Warning is logged.
1a. **Given** a reading already stored by an earlier run that appears again with a different value, **Then** the stored value is kept, it counts as AlreadyStored, and a Warning is logged.
2. **Given** lines that are malformed JSON, not objects, miss required fields, have wrong types (including a string value), or non-finite values, **When** processed, **Then** each is rejected with its reason code and counted, and processing continues.
3. **Given** an unknown or wrong-case metric, an empty or whitespace deviceId, or a negative, out-of-range or non-integer-token seq (including `5.0`), **When** processed, **Then** it is rejected and never reaches rule evaluation.
4. **Given** a timestamp with `Z` or an offset (with or without fractional seconds), **Then** it is accepted and normalized to UTC; **Given** a timestamp with no offset, **Then** it is rejected as InvalidTimestamp.
5. **Given** a well-formed but physically implausible value (e.g. negative vibration), **Then** it is valid data that rules may classify.
6. **Given** blank lines, a UTF-8 BOM or CRLF endings, **Then** the file is read correctly; blank lines are counted as read but neither parsed nor invalid.
7. **Given** invalid or duplicate readings, **Then** they are never counted as unacceptable.

---

### User Story 2 - Rule-based classification (Priority: P1)

As a rule administrator, I want readings classified against rules defined in a seed file, so that
adding, enabling or disabling a rule is a data change only.

**Why this priority**: Classification is the core business outcome and gates aggregation.

**Independent Test**: Load a fixture rule set covering each operator and applicability case; classify
fixture readings and compare to expected acceptable/unacceptable outcomes and reasons.

**Acceptance Scenarios**:

1. **Given** a GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual or Equal rule, **When** a reading satisfies its stated condition, **Then** the rule fires: the reading is unacceptable and the result names the rule with a human-readable reason (e.g. "temperature GreaterThan 100 fires at 103").
2. **Given** a Between rule with min and max, **When** a reading is below min or above max, **Then** it is a violation; a value equal to min or max is acceptable.
3. **Given** a rule with a deviceId, **Then** it applies only to that device; **Given** a rule without a deviceId, **Then** it applies to all devices carrying that metric. A rule never applies to a different metric.
4. **Given** a disabled rule, **Then** it is never evaluated.
5. **Given** a reading violating several rules (per-reading and/or sustained), **Then** all violated rules are reported as the union.
6. **Given** a reading to which no enabled rule applies, **Then** it is acceptable.
7. **Given** a rule file with a duplicate id, unknown operator, missing or invalid parameter, min > max, durationSeconds <= 0, or unknown metric, **Then** startup fails with a clear message naming the offending rule.
8. **Given** a rule referencing a device with no data, **Then** it is valid and never fires.
9. **Given** a new operator type, **Then** it can be added without modifying existing evaluation code.

---

### User Story 3 - Sustained-condition detection with alerts (Priority: P1)

As an operations engineer, I want one alert per sustained episode, so that I'm notified of real
problems without noise.

A sustained rule (SustainedAbove) is violated when the metric stays strictly above a threshold for
at least durationSeconds of event time, tracked per (deviceId, metric).

**Why this priority**: This is the differentiating, hardest behavior and the reason alerts exist.

**Independent Test**: Feed fixture readings forming episodes of various lengths, in sorted and
shuffled order; verify alerts (fields, count) and classifications are identical across orders.

**Acceptance Scenarios**:

1. **Given** readings staying above threshold for at least the duration, **When** processed, **Then** one alert is produced with ruleId, ruleName, deviceId, metric, startTs (first above reading), endTs (first subsequent reading at or below threshold, or the last observed reading if data ends first), peakValue, and OpenAtEndOfData.
2. **Given** readings above threshold for less than the duration, **Then** no alert is produced and the readings stay acceptable (with respect to this rule).
3. **Given** the same readings in shuffled file order, **Then** alerts and classifications are identical.
4. **Given** a value exactly equal to the threshold, **Then** it is not above and ends the episode; **Given** an episode lasting exactly durationSeconds, **Then** it is sustained.
5. **Given** a single above reading followed by a non-above reading, **Then** the episode duration is the gap between them.
6. **Given** a gap in data between two above readings, **Then** the episode is not split.
7. **Given** a second episode whose startTs is less than the cooldown (default 5 minutes) after the previous alert's startTs for the same (rule, deviceId, metric), **Then** no new alert is produced and the suppression is counted and logged; **Given** an episode at or beyond the cooldown, **Then** a separate alert is produced. A suppressed episode's readings are still unacceptable.
8. **Given** different devices or metrics, **Then** their state and cooldowns are independent.
9. **Given** readings with equal ts, **Then** they are ordered by seq ascending.
10. **Given** every reading with ts in [startTs, endTs) of a sustained episode, **Then** it is unacceptable with the rule and an episode-start reason; the ending reading is not part of the episode.
11. **Given** a later run containing earlier-timestamped readings for an existing series, **Then** that series is re-evaluated from all stored readings without order-dependent or duplicate results.

---

### User Story 4 - Idempotent re-processing (Priority: P2)

As an operations engineer, I want to re-run the same file safely, so that retries never create
duplicate data.

**Why this priority**: Operational safety; required for retries but secondary to first-run correctness.

**Independent Test**: Process a fixture twice and compare stored readings, rule results and alerts
(count and content) between runs.

**Acceptance Scenarios**:

1. **Given** a file already processed, **When** processed again, **Then** stored readings, rule results and alerts are unchanged in count and content, with no orphan results or alerts.
2. **Given** the re-run, **Then** the report shows newly stored = 0 and already stored = the previous stored count, and parsed = newly stored + already stored + file duplicates.
3. **Given** the README, **Then** it states the keying strategy for readings, rule results and alerts, and a test demonstrates it.

---

### User Story 5 - Aggregation API (Priority: P2)

As an API client, I want time-bucketed statistics for acceptable readings, so that I can chart
clean data.

**Why this priority**: The consumer-facing output; depends on classification being correct.

**Independent Test**: Request aggregates over fixture data and compare to a hand-computed oracle.

**Acceptance Scenarios**:

1. **Given** `GET /api/aggregations` with deviceId, metric, from, to and bucketSeconds, **When** requested, **Then** the response lists buckets starting at from + i × bucketSeconds with bucket start, count, average, min and max over acceptable readings with from <= ts < to.
2. **Given** unacceptable readings in range, **Then** they are excluded from every statistic.
3. **Given** empty buckets, **Then** they are reported with count 0 and null average, min and max.
4. **Given** a reading exactly on a bucket boundary, **Then** it belongs to the bucket starting at that instant; a reading at `to` is excluded.
5. **Given** missing parameters, unparseable timestamps, from >= to, bucketSeconds <= 0, an unknown metric, or more than 10,000 buckets, **Then** the response is a clear 400 error.
6. **Given** an unknown device with a valid metric, **Then** the response is 200 with all buckets empty.
7. **Given** averages, **Then** they are returned unrounded.

---

### User Story 6 - Processing report and observability (Priority: P3)

As an operations engineer, I want an end-of-run report, so that I can verify what happened.

**Why this priority**: Verification aid; valuable but depends on all counted behaviors existing.

**Independent Test**: Process a fixture with known counts and assert every report figure and the reconciliation identities.

**Acceptance Scenarios**:

1. **Given** a completed run, **Then** the report, printed to the console and logged as a structured entry at the end of ingestion, contains: lines read, blank lines skipped, parsed, invalid rejected (per-reason breakdown), file duplicates removed, already stored, newly stored, rules loaded (enabled and total), rule evaluations performed, acceptable, unacceptable, rule violations, alerts generated, and alerts suppressed by cooldown.
2. **Given** the report, **Then** lines = blank + parsed + invalid; parsed = newly stored + already stored + file duplicates; newly stored + already stored = acceptable + unacceptable.
3. **Given** significant events, **Then** invalid records and conflicting duplicates are logged as Warning; rules loaded, episodes detected, alerts raised, suppressed alerts and the report as Information; per-reading detail as Debug.
4. **Given** the report has printed, **Then** the API remains available.

---

### Edge Cases

- Empty file, blank lines, a trailing line without a newline, a UTF-8 BOM, and Windows line endings (see User Story 1).
- Timestamps with offsets or fractional seconds are accepted; timestamps without any offset are rejected.
- A single reading for a (device, metric) pair, and a sustained episode still open at the end of data (OpenAtEndOfData).
- Identical timestamps with different seq values: distinct readings, not duplicates.
- Duplicate quadruple with a different value: lowest value wins within a file, stored value wins across runs; each conflict logged as Warning.
- Extremely large or small numbers, NaN or infinity, and numbers sent as strings (rejected as InvalidType / NonFiniteValue).
- Rules whose metric or device matches no data.
- Overlapping rules on the same metric, such as a Between rule plus a sustained rule (union of violations).
- Readings far in the past or future relative to the rest of the data.
- A bucket request spanning more than 10,000 buckets (400).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST process the input file at startup (configurable path), persist processed readings, rule results and alerts, print the processing report, then serve the aggregation API.
- **FR-002**: The system MUST reject and count malformed and semantically invalid readings, each with a stable reason code (MalformedJson, MissingField, InvalidType, UnknownMetric, InvalidTimestamp, InvalidSeq, NonFiniteValue), without terminating.
- **FR-003**: The system MUST remove duplicates by the (deviceId, metric, ts, seq) identity; for conflicting duplicates within a file it MUST keep the lowest value (order-independent) and log a Warning; a reading already stored by an earlier run keeps its stored value, with a Warning if the new value differs.
- **FR-004**: Invalid and duplicate readings MUST never be evaluated or counted as unacceptable.
- **FR-005**: Rules MUST be loaded from rules.json at startup and validated (fail fast, naming the offending rule); enabling, disabling, adding or changing rules requires no code change.
- **FR-006**: The system MUST support GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Equal (exact), Between (inclusive acceptable range) and SustainedAbove; operator names match case-insensitively.
- **FR-007**: A rule MUST apply by metric and, optionally, by device.
- **FR-008**: Every valid, non-duplicate reading MUST be classified acceptable or unacceptable; the violated rules are the union of per-reading and sustained rules, each with a reason. A reading with no applicable rule is acceptable.
- **FR-009**: The system MUST order each (deviceId, metric) series by (ts, seq) before stateful evaluation, so results are independent of file order.
- **FR-010**: A sustained episode (value > threshold continuously, endTs − startTs >= durationSeconds) MUST produce exactly one alert with ruleId, ruleName, deviceId, metric, startTs, endTs, peakValue and OpenAtEndOfData; every reading in [startTs, endTs) MUST be unacceptable.
- **FR-011**: Alerts MUST be deduplicated per (ruleId, deviceId, metric) by a cooldown measured from the previous alert's startTs, default 5 minutes and configurable.
- **FR-012**: Acceptable and unacceptable readings MUST be available as separate outputs.
- **FR-013**: Re-processing the same input MUST NOT create duplicate readings, rule results or alerts; keys are reading = (deviceId, metric, ts, seq), rule result = reading key + ruleId, alert = (ruleId, deviceId, metric, startTs). Affected series MUST be re-evaluated from all stored readings with results upserted by key.
- **FR-014**: `GET /api/aggregations` MUST return per-bucket start, count, average, min and max over acceptable readings in [from, to), with buckets aligned to `from`, empty buckets reported with count 0 and null statistics, and 400 for invalid parameters.
- **FR-015**: The system MUST print a reconciled processing report (including already stored and suppressed-alert counts) and log significant events at the specified levels.
- **FR-016**: Adding a new operator, input source or aggregation MUST NOT require changes to existing domain logic.

### Key Entities

- **Reading**: deviceId, metric, ts (UTC), value, seq. Identity is (deviceId, metric, ts, seq).
- **Rule**: id, name, enabled, metric, optional deviceId, operator, operator parameters (value; min and max; threshold and durationSeconds).
- **RuleResult** (a stored violation record; satisfied rules are counted as evaluations but not stored): reading reference, rule reference, reason. Key: reading key + ruleId.
- **Classification**: acceptable or unacceptable per reading, with the violated rules.
- **Alert**: ruleId, ruleName, deviceId, metric, startTs, endTs, peakValue, OpenAtEndOfData. Key: (ruleId, deviceId, metric, startTs).
- **ProcessingReport**: the counts listed in User Story 6.
- **AggregationBucket**: bucket start, count, average, min, max (null when empty).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On the supplied file, 100% of lines are accounted for as blank, stored, already stored, duplicate or invalid, and the report invariants hold exactly.
- **SC-002**: Shuffling the file order yields identical classifications and alerts.
- **SC-003**: Processing the same file twice leaves all stored counts unchanged and reports zero newly stored readings on the second run.
- **SC-004**: Each sustained episode in the supplied data produces exactly one alert with correct start and end times, and cooldown suppresses the repeats.
- **SC-005**: Aggregation results over acceptable readings match a hand-computed oracle on fixtures, and unacceptable readings never appear in them.
- **SC-006**: The supplied file processes in seconds on a standard laptop.
- **SC-007**: Every policy recorded in Clarifications appears in the README.

## Assumptions

- Single-run, batch-style processing of one local file is sufficient.
- Implementation constraints (architecture, storage, testing) are governed by the project constitution and addressed in the plan, not this spec.
- Where a decision is not recorded in Clarifications, the simplest defensible option is chosen and justified in the README.

## Out of Scope

Message queues or brokers, Kubernetes, authentication, any user interface, production deployment, and rule-management CRUD endpoints.
