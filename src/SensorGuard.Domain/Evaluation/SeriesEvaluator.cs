using System;
using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Rules;

namespace SensorGuard.Domain.Evaluation;

/// <summary>
/// Classifies readings against rules. Readings are grouped per (device, metric) series and ordered by event time
/// (timestamp, then seq), so the result never depends on input order (Principle III). Per-reading rules run on every
/// reading; stateful rules run once per series and yield episodes, whose readings are violations and which the
/// <see cref="AlertPolicy"/> turns into deduplicated alerts. Output is deterministic: series by device then metric,
/// readings by (timestamp, seq), violations in rule order.
/// </summary>
public sealed class SeriesEvaluator
{
    private readonly OperatorRegistry _registry;
    private readonly AlertPolicy _alertPolicy;

    public SeriesEvaluator(OperatorRegistry registry, AlertPolicy alertPolicy)
    {
        _registry = registry;
        _alertPolicy = alertPolicy;
    }

    public SeriesEvaluation Evaluate(IEnumerable<Reading> readings, IReadOnlyList<Rule> rules)
    {
        var classifications = new List<ReadingClassification>();
        var allViolations = new List<RuleViolation>();
        var detected = new List<DetectedEpisode>();
        var alerts = new List<Alert>();
        var suppressed = new List<SuppressedEpisode>();
        var evaluations = 0;

        foreach (var series in GroupIntoOrderedSeries(readings))
        {
            var perReading = new Dictionary<ReadingKey, List<RuleViolation>>();

            foreach (var rule in rules.Where(r => r.AppliesTo(series.Key)))
            {
                if (rule.Kind == OperatorKind.Stateless)
                {
                    evaluations += EvaluateStateless(rule, series.Readings, perReading);
                }
                else
                {
                    evaluations++;
                    EvaluateStateful(rule, series, perReading, detected, alerts, suppressed);
                }
            }

            foreach (var reading in series.Readings)
            {
                var violations = perReading.TryGetValue(reading.Key, out var found) ? found : new List<RuleViolation>();
                classifications.Add(new ReadingClassification(
                    reading.Key,
                    violations.Count == 0 ? Classification.Acceptable : Classification.Unacceptable,
                    violations));
                allViolations.AddRange(violations);
            }
        }

        return new SeriesEvaluation(classifications, allViolations, detected, alerts, suppressed, evaluations);
    }

    private int EvaluateStateless(
        Rule rule, IReadOnlyList<Reading> readings, Dictionary<ReadingKey, List<RuleViolation>> perReading)
    {
        var op = _registry.FindStateless(rule.OperatorName)
            ?? throw new InvalidOperationException($"Operator '{rule.OperatorName}' of rule '{rule.Id}' is not registered.");
        foreach (var reading in readings)
        {
            if (op.IsViolated(reading.Value, rule))
            {
                AddViolation(perReading, reading.Key, rule.Id, op.Describe(reading.Value, rule));
            }
        }

        return readings.Count;
    }

    private void EvaluateStateful(
        Rule rule,
        OrderedSeries series,
        Dictionary<ReadingKey, List<RuleViolation>> perReading,
        List<DetectedEpisode> detected,
        List<Alert> alerts,
        List<SuppressedEpisode> suppressed)
    {
        var op = _registry.FindStateful(rule.OperatorName)
            ?? throw new InvalidOperationException($"Operator '{rule.OperatorName}' of rule '{rule.Id}' is not registered.");
        var episodes = op.Evaluate(rule, series.Readings);

        foreach (var episode in episodes)
        {
            detected.Add(new DetectedEpisode(rule.Id, rule.Name, episode));
            var reason = op.Describe(rule, episode);
            foreach (var key in episode.Covered)
            {
                AddViolation(perReading, key, rule.Id, reason);
            }
        }

        var decision = _alertPolicy.Apply(rule, episodes);
        alerts.AddRange(decision.Raised);
        suppressed.AddRange(decision.Suppressed);
    }

    private static void AddViolation(
        Dictionary<ReadingKey, List<RuleViolation>> perReading, ReadingKey key, RuleId rule, string reason)
    {
        if (!perReading.TryGetValue(key, out var list))
        {
            list = new List<RuleViolation>();
            perReading[key] = list;
        }

        list.Add(new RuleViolation(key, rule, reason));
    }

    private static IEnumerable<OrderedSeries> GroupIntoOrderedSeries(IEnumerable<Reading> readings) =>
        readings
            .GroupBy(r => r.Series)
            .OrderBy(g => g.Key.Device.Value, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Metric)
            .Select(g => new OrderedSeries(
                g.Key,
                g.OrderBy(r => r.Key.Timestamp).ThenBy(r => r.Key.Seq).ToList()));

    private sealed record OrderedSeries(SeriesKey Key, IReadOnlyList<Reading> Readings);
}
