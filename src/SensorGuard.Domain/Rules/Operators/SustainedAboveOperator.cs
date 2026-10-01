using System;
using System.Collections.Generic;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Rules.Operators;

/// <summary>
/// Violated when a metric stays strictly above <c>threshold</c> for at least <c>durationSeconds</c> of event time.
/// A single ordered scan per (device, metric) series (research R8): an episode starts at the first reading above the
/// threshold, ends at the first later reading at or below it (or at the last reading when the data ends first, flagged
/// open), and holds the last value across gaps. Duration is inclusive.
/// </summary>
public sealed class SustainedAboveOperator : IStatefulOperator
{
    private const string ThresholdParameter = "threshold";
    private const string DurationParameter = "durationSeconds";

    public string Name => "SustainedAbove";

    public IReadOnlyList<string> Validate(RuleParameters parameters)
    {
        var errors = new List<string>(parameters.RequireFinite(ThresholdParameter, DurationParameter));
        if (errors.Count == 0 && parameters.Get(DurationParameter) <= 0)
        {
            errors.Add("durationSeconds must be greater than 0");
        }

        return errors;
    }

    public IReadOnlyList<Episode> Evaluate(Rule rule, IReadOnlyList<Reading> orderedSeries)
    {
        var threshold = rule.Parameters.Get(ThresholdParameter);
        var minimum = TimeSpan.FromSeconds(rule.Parameters.Get(DurationParameter));
        var episodes = new List<Episode>();

        DateTimeOffset? start = null;
        var peak = 0.0;
        var covered = new List<ReadingKey>();

        foreach (var reading in orderedSeries)
        {
            if (reading.Value > threshold)
            {
                if (start is null)
                {
                    start = reading.Key.Timestamp;
                    peak = reading.Value;
                    covered = new List<ReadingKey>();
                }

                peak = Math.Max(peak, reading.Value);
                covered.Add(reading.Key);
            }
            else if (start is not null)
            {
                AddIfSustained(reading.Key.Timestamp, openAtEnd: false);
                start = null;
            }
        }

        if (start is not null)
        {
            AddIfSustained(orderedSeries[^1].Key.Timestamp, openAtEnd: true);
        }

        return episodes;

        void AddIfSustained(DateTimeOffset end, bool openAtEnd)
        {
            if (end - start!.Value >= minimum)
            {
                episodes.Add(new Episode(orderedSeries[0].Series, start.Value, end, peak, openAtEnd, covered));
            }
        }
    }

    public string Describe(Rule rule, Episode episode) =>
        $"above {RuleParameters.Format(rule.Parameters.Get(ThresholdParameter))} for {RuleParameters.Format(rule.Parameters.Get(DurationParameter))}s+ (episode started {TimestampText.Format(episode.StartTs)})";
}
