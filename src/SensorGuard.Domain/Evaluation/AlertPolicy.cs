using System;
using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Rules;

namespace SensorGuard.Domain.Evaluation;

/// <summary>
/// Cooldown deduplication (Principle V). For one rule, per (device, metric): an episode raises an alert unless a
/// previously RAISED alert for the same key started less than the cooldown earlier. The anchor is the raised alert's
/// start time and moves only when an alert is raised, so a chain of closely spaced episodes alerts once per cooldown.
/// </summary>
public sealed class AlertPolicy
{
    private readonly TimeSpan _cooldown;

    public AlertPolicy(TimeSpan cooldown) => _cooldown = cooldown;

    public AlertDecision Apply(Rule rule, IEnumerable<Episode> episodes)
    {
        var raised = new List<Alert>();
        var suppressed = new List<SuppressedEpisode>();

        var bySeries = episodes
            .GroupBy(e => e.Series)
            .OrderBy(g => g.Key.Device.Value, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Metric);

        foreach (var series in bySeries)
        {
            Alert? anchor = null;
            foreach (var episode in series.OrderBy(e => e.StartTs))
            {
                if (anchor is not null && episode.StartTs - anchor.StartTs < _cooldown)
                {
                    suppressed.Add(new SuppressedEpisode(rule.Id, rule.Name, episode, anchor));
                    continue;
                }

                anchor = new Alert(rule.Id, rule.Name, episode.Series, episode.StartTs, episode.EndTs, episode.Peak, episode.OpenAtEnd);
                raised.Add(anchor);
            }
        }

        return new AlertDecision(raised, suppressed);
    }
}
