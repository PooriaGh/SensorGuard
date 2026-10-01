using System.Collections.Generic;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Rules;

/// <summary>A per-reading operator: decides from one value whether the rule is violated.</summary>
public interface IStatelessOperator
{
    string Name { get; }

    /// <summary>Problems with the rule's parameters; empty when the parameters are usable.</summary>
    IReadOnlyList<string> Validate(RuleParameters parameters);

    bool IsViolated(double value, Rule rule);

    string Describe(double value, Rule rule);
}

/// <summary>
/// A stateful operator: sees one ordered (device, metric) series and returns the episodes in which the rule
/// is violated. Alerting and cooldown are separate concerns (<see cref="AlertPolicy"/>).
/// </summary>
public interface IStatefulOperator
{
    string Name { get; }

    IReadOnlyList<string> Validate(RuleParameters parameters);

    /// <param name="orderedSeries">Readings of one series sorted by (timestamp, seq).</param>
    IReadOnlyList<Episode> Evaluate(Rule rule, IReadOnlyList<Reading> orderedSeries);

    /// <summary>Human-readable reason attached to every reading covered by the episode.</summary>
    string Describe(Rule rule, Episode episode);
}
