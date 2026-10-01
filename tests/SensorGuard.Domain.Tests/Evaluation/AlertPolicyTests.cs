using System;
using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Rules;
using SensorGuard.Domain.Tests.Support;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Evaluation;

public sealed class AlertPolicyTests
{
    private static readonly DateTimeOffset T0 = Readings.At("2025-06-01T08:00:00Z");
    private readonly AlertPolicy _policy = new(TimeSpan.FromMinutes(5));
    private readonly Rule _rule = TestRules.Sustained("S1", Metric.Temperature, 80, 30);

    private static SeriesKey SeriesOf(string device = "PUMP-01", Metric metric = Metric.Temperature) =>
        new(new DeviceId(device), metric);

    private static Episode Ep(double startMinutes, double lengthSeconds = 60, double peak = 90, string device = "PUMP-01", Metric metric = Metric.Temperature) =>
        new(
            SeriesOf(device, metric),
            T0.AddMinutes(startMinutes),
            T0.AddMinutes(startMinutes).AddSeconds(lengthSeconds),
            peak,
            false,
            Array.Empty<ReadingKey>());

    [Fact]
    public void One_episode_raises_one_alert_carrying_all_alert_fields()
    {
        var episode = new Episode(SeriesOf(), T0, T0.AddSeconds(90), 97.5, true, Array.Empty<ReadingKey>());

        var alert = _policy.Apply(_rule, new[] { episode }).Raised.Single();

        alert.Rule.ShouldBe(new RuleId("S1"));
        alert.RuleName.ShouldBe("S1 name");
        alert.Series.ShouldBe(SeriesOf());
        alert.StartTs.ShouldBe(T0);
        alert.EndTs.ShouldBe(T0.AddSeconds(90));
        alert.PeakValue.ShouldBe(97.5);
        alert.OpenAtEndOfData.ShouldBeTrue();
    }

    [Fact]
    public void A_second_episode_inside_the_cooldown_is_suppressed_and_references_the_alert_that_suppressed_it()
    {
        var decision = _policy.Apply(_rule, new[] { Ep(0), Ep(4.9) });

        decision.Raised.Count.ShouldBe(1);
        var suppressed = decision.Suppressed.Single();
        suppressed.Episode.StartTs.ShouldBe(T0.AddMinutes(4.9));
        suppressed.SuppressedBy.StartTs.ShouldBe(T0);
    }

    [Fact]
    public void A_second_episode_exactly_one_cooldown_after_the_previous_alert_start_raises()
    {
        var decision = _policy.Apply(_rule, new[] { Ep(0), Ep(5) });

        decision.Raised.Select(a => a.StartTs).ShouldBe(new[] { T0, T0.AddMinutes(5) });
        decision.Suppressed.ShouldBeEmpty();
    }

    [Fact]
    public void Episodes_separated_by_more_than_the_cooldown_yield_separate_alerts()
    {
        _policy.Apply(_rule, new[] { Ep(0), Ep(20) }).Raised.Count.ShouldBe(2);
    }

    [Fact]
    public void The_cooldown_is_anchored_on_the_previous_RAISED_alert_start_not_on_a_suppressed_episode()
    {
        // 0 raises; 4 is suppressed (anchor stays 0); 8 raises (8 - 0 >= 5); had 4 moved the anchor, 8 would be suppressed.
        var decision = _policy.Apply(_rule, new[] { Ep(0), Ep(4), Ep(8) });

        decision.Raised.Select(a => a.StartTs).ShouldBe(new[] { T0, T0.AddMinutes(8) });
        decision.Suppressed.Single().Episode.StartTs.ShouldBe(T0.AddMinutes(4));
    }

    [Fact]
    public void Cooldown_is_independent_per_rule_device_and_metric()
    {
        var other = TestRules.Sustained("S2", Metric.Temperature, 80, 30);

        var a = _policy.Apply(_rule, new[] { Ep(0, device: "PUMP-01"), Ep(1, device: "PUMP-02"), Ep(2, device: "PUMP-02", metric: Metric.Pressure) });
        var b = _policy.Apply(other, new[] { Ep(1) });

        a.Raised.Count.ShouldBe(3);
        b.Raised.Count.ShouldBe(1);
    }

    [Fact]
    public void A_series_cooldown_does_not_suppress_other_series_episodes_inside_the_window()
    {
        var decision = _policy.Apply(_rule, new[] { Ep(0, device: "PUMP-01"), Ep(1, device: "PUMP-01"), Ep(1, device: "PUMP-02") });

        decision.Raised.Select(a => a.Series.Device.Value).OrderBy(d => d).ShouldBe(new[] { "PUMP-01", "PUMP-02" });
        decision.Suppressed.Single().Episode.Series.Device.Value.ShouldBe("PUMP-01");
    }

    [Fact]
    public void The_cooldown_is_configurable()
    {
        var oneMinute = new AlertPolicy(TimeSpan.FromMinutes(1));

        oneMinute.Apply(_rule, new[] { Ep(0), Ep(1) }).Raised.Count.ShouldBe(2);
        new AlertPolicy(TimeSpan.FromMinutes(10)).Apply(_rule, new[] { Ep(0), Ep(9) }).Raised.Count.ShouldBe(1);
    }

    [Fact]
    public void The_outcome_does_not_depend_on_the_order_the_episodes_are_supplied_in()
    {
        var episodes = new List<Episode> { Ep(8), Ep(0), Ep(4), Ep(20) };

        var forward = _policy.Apply(_rule, episodes);
        var reversed = _policy.Apply(_rule, Enumerable.Reverse(episodes));

        reversed.Raised.Select(a => a.StartTs).ShouldBe(forward.Raised.Select(a => a.StartTs));
        reversed.Suppressed.Select(s => s.Episode.StartTs).ShouldBe(forward.Suppressed.Select(s => s.Episode.StartTs));
    }

    [Fact]
    public void Alerts_are_ordered_deterministically_by_series_then_start()
    {
        var decision = _policy.Apply(_rule, new[] { Ep(10, device: "B"), Ep(0, device: "B"), Ep(5, device: "A") });

        decision.Raised.Select(a => (a.Series.Device.Value, a.StartTs)).ShouldBe(
            new[] { ("A", T0.AddMinutes(5)), ("B", T0), ("B", T0.AddMinutes(10)) });
    }
}
