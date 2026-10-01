using System;
using System.Collections.Generic;
using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Evaluation;

/// <summary>A reading violated a rule. Identity (idempotency key) is the reading key plus the rule id.</summary>
public sealed record RuleViolation(ReadingKey Reading, RuleId Rule, string Reason);

public sealed record ReadingClassification(
    ReadingKey Key,
    Classification Classification,
    IReadOnlyList<RuleViolation> Violations);

/// <summary>
/// A period in which a stateful rule was violated: starts at the first reading above the threshold, ends at the first
/// later reading at or below it (or at the last observed reading when the data ends first).
/// </summary>
public sealed record Episode(
    SeriesKey Series,
    DateTimeOffset StartTs,
    DateTimeOffset EndTs,
    double Peak,
    bool OpenAtEnd,
    IReadOnlyList<ReadingKey> Covered);

/// <summary>An episode of one rule, as detected by the evaluator.</summary>
public sealed record DetectedEpisode(RuleId Rule, string RuleName, Episode Episode);

/// <summary>One alert per sustained episode. Identity (idempotency key): (rule, device, metric, startTs).</summary>
public sealed record Alert(
    RuleId Rule,
    string RuleName,
    SeriesKey Series,
    DateTimeOffset StartTs,
    DateTimeOffset EndTs,
    double PeakValue,
    bool OpenAtEndOfData);

public sealed record SuppressedEpisode(RuleId Rule, string RuleName, Episode Episode, Alert SuppressedBy);

public sealed record AlertDecision(IReadOnlyList<Alert> Raised, IReadOnlyList<SuppressedEpisode> Suppressed);

public sealed record SeriesEvaluation(
    IReadOnlyList<ReadingClassification> Classifications,
    IReadOnlyList<RuleViolation> Violations,
    IReadOnlyList<DetectedEpisode> Episodes,
    IReadOnlyList<Alert> Alerts,
    IReadOnlyList<SuppressedEpisode> Suppressed,
    int RuleEvaluations);
