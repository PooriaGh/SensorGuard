# SensorGuard Constitution

SensorGuard is a backend service (C# / .NET 8 or 10) that ingests numeric IoT sensor readings from a
JSONL file, cleans messy data, evaluates readings against data-driven rules (including a stateful
time-based operator), raises deduplicated alerts, and exposes time-bucketed aggregates of acceptable
data via a GET API. Scope is a two-working-day technical task: every decision MUST favor
correctness and clarity over breadth.

## Core Principles

### I. Domain-First, Infrastructure-Independent Architecture (NON-NEGOTIABLE)

- The solution MUST use Clean Architecture with projects Domain, Application, Infrastructure and
  Api. Dependencies MUST point inward only. Domain MUST have zero dependencies on frameworks,
  storage, I/O, logging frameworks or serialization.
- Validation, deduplication, rule applicability, rule evaluation, sustained-state management,
  alerting and classification MUST live in Domain/Application. They MUST NOT live in controllers,
  endpoints or repositories.
- Infrastructure (file reader, storage engine, JSON parsing) MUST be accessed only through
  interfaces defined inward, so new input sources, storage engines and aggregations can be added
  without touching domain code.
- Out of scope and MUST NOT be built: message brokers, Kubernetes, authentication, any UI,
  production deployment, rule-management CRUD endpoints, and generic repositories. Speculative
  infrastructure MUST NOT be added.

### II. Messy Data Is the Core Problem

- Every input line is untrusted. Malformed JSON, missing fields, wrong types, unparseable
  timestamps, non-finite values, and semantically invalid readings MUST be rejected and counted.
  They MUST NOT crash the service or abort the run.
- A rule violation is not malformed data. Invalid and duplicate readings MUST be rejected before
  rule evaluation and MUST NOT be counted as unacceptable.
- Duplicate identity is the (deviceId, metric, ts, seq) quadruple. The duplicate policy
  (first-wins or last-wins) MUST be chosen deliberately, justified, and documented in the README.
- Every rejection MUST be recorded with a reason and logged at an appropriate level.

### III. Deterministic, Event-Time Stateful Evaluation

- The input file is not time-sorted. Results MUST depend only on event time (`ts`), never on file
  or processing order: the same data in any order MUST yield the same classification and alerts.
- The ordering strategy (sort-then-scan batch, or incremental streaming with a bounded lateness
  window) is a deliberate decision. Its trade-offs, including the treatment of late arrivals,
  MUST be documented.
- SustainedAbove is violated when the metric stays strictly above threshold for at least
  durationSeconds of event time. The window starts when the value first crosses the threshold and
  ends when it drops to or below it. State MUST be kept per (deviceId, metric). Boundary behavior
  (exactly at the threshold, exactly at the duration) MUST be explicitly defined and tested.

### IV. Rules Are Data, Engine Is Open for Extension

- Rules MUST be loaded at startup from the rules.json seed file. Adding, enabling or disabling a
  rule is a data change, never a code change.
- Operators MUST be pluggable (Strategy / registry). Adding an operator means adding a new class
  and registering it, with no modification to existing evaluation code (Open/Closed). The README
  MUST explain how to add one.
- Required operators: GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Equal, Between,
  and the stateful SustainedAbove.
- A rule applies to a metric, and optionally to one device. A rule without a deviceId applies to
  all devices carrying that metric.
- A reading is acceptable if it satisfies all applicable enabled rules. It is unacceptable if at
  least one is violated, and MUST identify the violated rule(s) with a meaningful reason. When no
  rule applies the reading is acceptable; this policy MUST be documented.
- Invalid rule definitions in the seed file MUST fail fast at startup with a clear message.

### V. Alerts Represent Episodes, Not Readings

- Stateful operators MUST produce alert records, not per-reading noise. One sustained episode maps
  to one alert with ruleId, deviceId, metric, startTs, endTs (or end of observed data) and peak
  value.
- Cooldown deduplication is a domain rule: after an alert for a given (rule, deviceId, metric), no
  new alert is produced until the cooldown (default 5 minutes, configurable) has elapsed. Episodes
  separated by more than the cooldown yield separate alerts. The exact semantics MUST be
  documented.
- Alerts are stored alongside readings and rule results, and MUST obey Principle VI.

### VI. Idempotency by Construction (NON-NEGOTIABLE)

- Processing the same input twice MUST create no duplicate readings, rule results or alerts. This
  MUST be enforced with deterministic natural keys (readings by the dedup quadruple, rule results
  by reading key + ruleId, alerts by ruleId + deviceId + metric + startTs) and unique constraints
  or upsert semantics, not with "check then insert" tricks.
- The keying strategy MUST be stated in the README and proven by an automated re-run test.

### VII. Test-First, Behavior-Focused Testing (NON-NEGOTIABLE)

- Tests MUST be written before implementation (red, green, refactor). Domain logic MUST be tested
  with fast, infrastructure-free unit tests.
- Mandatory tests: deduplication, invalid-record rejection, every operator, rule applicability,
  SustainedAbove with out-of-order data and boundary cases, alert cooldown, idempotent re-run,
  aggregation (bucket boundaries, acceptable-only, empty-bucket policy), and the processing report
  counts.
- Tests MUST use deterministic data and an injected clock or time source. They MUST NOT depend on
  wall-clock time, randomness or file-system state outside fixtures.

### VIII. Aggregation Uses Only Acceptable Data

- The GET aggregation endpoint takes deviceId, metric, from, to and bucket size, and returns
  per-bucket start, count, average, min and max over acceptable readings only, within the
  half-open interval [from, to).
- Unacceptable readings MUST be excluded. Empty-bucket behavior (omit or zero-fill) MUST be chosen
  and documented. Bad query parameters MUST return clear 400 responses.

### IX. Observability and Honest Reporting

- At the end of every run a processing report with real numbers MUST be printed: total lines
  read, parsed readings, stored readings, duplicates removed, invalid rejected, rules loaded, rule
  evaluations performed, acceptable, unacceptable, rule violations, alerts generated. The counts
  MUST reconcile with each other, and a test MUST assert the reconciliation.
- Structured logging MUST use appropriate levels: Warning for rejections, Information for
  sustained episodes and alerts, Debug for per-reading detail. Per-valid-reading noise MUST NOT be
  logged at Information.

### X. Clean, Simple, Explainable Code

- SOLID applies where it earns its place, with no over-engineering. Code MUST favor small,
  intention-revealing names, immutable value objects and records in the domain, explicit error
  handling, and no dead code or magic numbers (constants and configuration are named).
- Reasonable time and memory complexity is expected and trade-offs MUST be documented. The file
  SHOULD be streamed line by line instead of loaded whole where practical.
- Every significant decision MUST be explainable in a live walkthrough. If it cannot be explained
  simply, it MUST be simplified.

## Delivery and Process Standards

- Git history MUST be incremental and meaningful: small, focused commits following the spec-kit
  flow (constitution, spec, plan, tasks, implementation by slice). A single final commit MUST NOT
  be submitted. Commit messages MUST follow the Conventional Commits format.
- The README is a first-class deliverable. It MUST cover how to run and test, architectural style
  and trade-offs, storage choice and justification, rule model and evaluation policy, state and
  ordering strategy for SustainedAbove, alert cooldown policy, duplicate policy, idempotency
  keying, aggregation behavior, and a disclosure of AI tool usage.
- AI tools are allowed, but their use MUST be disclosed in the README, and every AI-generated line
  MUST be understood and owned by the author.
- Scope discipline: if a requirement seems to exceed the two-working-day budget, it MUST be raised
  early instead of silently cut.

## Governance

- This constitution supersedes other practices for the project. Any spec, plan or task that
  violates a principle MUST either be corrected or carry a written, justified exception in the
  plan's Complexity Tracking section.
- Principles I, VI and VII are non-negotiable and MUST NOT be waived.
- Amendments require a semantic version bump, a dated changelog entry and a rationale. MAJOR:
  principle removal or incompatible redefinition; MINOR: new or materially expanded principle;
  PATCH: clarification or wording.
- Compliance MUST be reviewed at plan time (Constitution Check) and before each slice is
  considered done.

**Version**: 1.0.0 | **Ratified**: 2026-10-01 | **Last Amended**: 2026-10-01
