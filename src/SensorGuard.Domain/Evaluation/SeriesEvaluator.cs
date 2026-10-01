using System;
using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Rules;

namespace SensorGuard.Domain.Evaluation;

/// <summary>
/// Classifies readings against rules. Readings are grouped per (device, metric) series and ordered by event time
/// (timestamp, then seq), so the result never depends on input order (Principle III). Per-reading rules run on every
/// reading; the output is deterministic: series by device then metric, readings by (timestamp, seq).
/// </summary>
public sealed class SeriesEvaluator
{
    private readonly OperatorRegistry _registry;

    public SeriesEvaluator(OperatorRegistry registry) => _registry = registry;

    public SeriesEvaluation Evaluate(IEnumerable<Reading> readings, IReadOnlyList<Rule> rules)
    {
        var classifications = new List<ReadingClassification>();
        var allViolations = new List<RuleViolation>();
        var evaluations = 0;

        foreach (var series in GroupIntoOrderedSeries(readings))
        {
            var applicable = rules.Where(r => r.AppliesTo(series.Key)).ToList();
            var perReading = new Dictionary<ReadingKey, List<RuleViolation>>();

            foreach (var rule in applicable.Where(r => r.Kind == OperatorKind.Stateless))
            {
                var op = _registry.FindStateless(rule.OperatorName)
                    ?? throw new InvalidOperationException($"Operator '{rule.OperatorName}' of rule '{rule.Id}' is not registered.");
                foreach (var reading in series.Readings)
                {
                    evaluations++;
                    if (op.IsViolated(reading.Value, rule))
                    {
                        AddViolation(perReading, reading.Key, rule.Id, op.Describe(reading.Value, rule));
                    }
                }
            }

            foreach (var reading in series.Readings)
            {
                var violations = perReading.TryGetValue(reading.Key, out var found)
                    ? found
                    : new List<RuleViolation>();
                classifications.Add(new ReadingClassification(
                    reading.Key,
                    violations.Count == 0 ? Classification.Acceptable : Classification.Unacceptable,
                    violations));
                allViolations.AddRange(violations);
            }
        }

        return new SeriesEvaluation(
            classifications,
            allViolations,
            Array.Empty<DetectedEpisode>(),
            Array.Empty<Alert>(),
            Array.Empty<SuppressedEpisode>(),
            evaluations);
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
