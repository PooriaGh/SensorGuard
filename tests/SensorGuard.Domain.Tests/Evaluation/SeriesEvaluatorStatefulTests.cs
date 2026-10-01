using System;
using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Rules;
using SensorGuard.Domain.Tests.Support;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Evaluation;

public sealed class SeriesEvaluatorStatefulTests
{
    private readonly SensorGuard.Domain.Evaluation.SeriesEvaluator _evaluator = TestRules.Evaluator();

    private static readonly Rule Sustained80For30 = TestRules.Sustained("S1", Metric.Temperature, 80, 30);

    // 08:00:00 start, 10 s step. Above 80 from index 2..6 (08:00:20..08:01:00): a 40+ s episode ending at index 7.
    private static List<Reading> EpisodeSeries(string device = "PUMP-01") =>
        Readings.Series(device, Metric.Temperature, "2025-06-01T08:00:00Z", 10, 70, 71, 85, 86, 87, 88, 89, 70, 70);

    private static string Summarize(SensorGuard.Domain.Evaluation.SeriesEvaluation e) =>
        string.Join(
            ";",
            e.Classifications.Select(c => $"{c.Key.Device}/{c.Key.Metric}/{c.Key.Timestamp:o}/{c.Key.Seq}={c.Classification}[{string.Join(",", c.Violations.Select(v => v.Rule + ":" + v.Reason))}]"))
        + "|"
        + string.Join(";", e.Alerts.Select(a => $"{a.Rule}/{a.Series}/{a.StartTs:o}/{a.EndTs:o}/{a.PeakValue}/{a.OpenAtEndOfData}"))
        + "|"
        + string.Join(";", e.Suppressed.Select(s => $"{s.Rule}/{s.Episode.Series}/{s.Episode.StartTs:o}"))
        + "|" + e.RuleEvaluations;

    [Fact]
    public void Readings_inside_a_sustained_episode_are_unacceptable_and_the_ending_reading_is_not()
    {
        var result = _evaluator.Evaluate(EpisodeSeries(), new[] { Sustained80For30 });

        result.Classifications.Select(c => c.Classification).ShouldBe(new[]
        {
            Classification.Acceptable,
            Classification.Acceptable,
            Classification.Unacceptable,
            Classification.Unacceptable,
            Classification.Unacceptable,
            Classification.Unacceptable,
            Classification.Unacceptable,
            Classification.Acceptable,
            Classification.Acceptable,
        });
    }

    [Fact]
    public void The_reason_names_the_threshold_the_duration_and_the_episode_start()
    {
        var result = _evaluator.Evaluate(EpisodeSeries(), new[] { Sustained80For30 });

        result.Violations.First().Reason.ShouldBe("above 80 for 30s+ (episode started 2025-06-01T08:00:20Z)");
        result.Violations.First().Rule.ShouldBe(new RuleId("S1"));
    }

    [Fact]
    public void A_sustained_episode_produces_exactly_one_alert()
    {
        var result = _evaluator.Evaluate(EpisodeSeries(), new[] { Sustained80For30 });

        var alert = result.Alerts.Single();
        alert.StartTs.ShouldBe(Readings.At("2025-06-01T08:00:20Z"));
        alert.EndTs.ShouldBe(Readings.At("2025-06-01T08:01:10Z"));
        alert.PeakValue.ShouldBe(89);
        alert.OpenAtEndOfData.ShouldBeFalse();
        result.Episodes.Single().Rule.ShouldBe(new RuleId("S1"));
    }

    [Fact]
    public void A_run_above_the_threshold_shorter_than_the_duration_leaves_readings_acceptable_and_raises_nothing()
    {
        var series = Readings.Series("PUMP-01", Metric.Temperature, "2025-06-01T08:00:00Z", 10, 70, 90, 91, 70, 70);

        var result = _evaluator.Evaluate(series, new[] { Sustained80For30 });

        result.Classifications.All(c => c.Classification == Classification.Acceptable).ShouldBeTrue();
        result.Alerts.ShouldBeEmpty();
    }

    [Fact]
    public void Sorted_and_shuffled_input_give_identical_classifications_and_alerts()
    {
        var input = EpisodeSeries("PUMP-01")
            .Concat(EpisodeSeries("PUMP-02"))
            .Concat(Readings.Series("PUMP-01", Metric.Pressure, "2025-06-01T08:00:00Z", 10, 5, 6, 12, 13, 14, 6))
            .ToList();
        var rules = new[]
        {
            Sustained80For30,
            TestRules.Stateless("R1", "GreaterThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 88.5) }),
            TestRules.Sustained("S2", Metric.Pressure, 10, 20, device: "PUMP-01"),
        };

        var baseline = Summarize(_evaluator.Evaluate(input, rules));

        foreach (var seed in new[] { 1, 7, 42, 20251 })
        {
            Summarize(_evaluator.Evaluate(Readings.Shuffled(input, seed), rules)).ShouldBe(baseline);
        }
    }

    [Fact]
    public void Sustained_and_stateless_rules_give_the_union_of_violations()
    {
        var rules = new[]
        {
            Sustained80For30,
            TestRules.Stateless("R1", "GreaterThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 88) }),
        };

        var result = _evaluator.Evaluate(EpisodeSeries(), rules);

        var peakReading = result.Classifications.Single(c => c.Key.Seq == 6); // 88 is not > 88; seq 7 is 89
        var topReading = result.Classifications.Single(c => c.Key.Seq == 7);
        peakReading.Violations.Select(v => v.Rule.Value).ShouldBe(new[] { "S1" });
        topReading.Violations.Select(v => v.Rule.Value).OrderBy(r => r).ShouldBe(new[] { "R1", "S1" });
    }

    [Fact]
    public void A_second_episode_inside_the_cooldown_raises_no_alert_but_its_readings_are_still_unacceptable()
    {
        // Episode 1 starts 08:00:20, episode 2 starts 08:02:20 (2 min later, inside the 5 min cooldown).
        var series = Readings.Series(
            "PUMP-01", Metric.Temperature, "2025-06-01T08:00:00Z", 10,
            70, 70, 85, 85, 85, 85, 70, 70, 70, 70, 70, 70, 70, 70, 85, 85, 85, 85, 70);

        var result = _evaluator.Evaluate(series, new[] { Sustained80For30 });

        result.Alerts.Count.ShouldBe(1);
        result.Suppressed.Count.ShouldBe(1);
        result.Classifications.Count(c => c.Classification == Classification.Unacceptable).ShouldBe(8);
        result.Episodes.Count.ShouldBe(2);
    }

    [Fact]
    public void A_rule_without_a_device_runs_per_series_so_devices_do_not_interact()
    {
        var input = EpisodeSeries("PUMP-01").Concat(EpisodeSeries("PUMP-02")).ToList();

        var result = _evaluator.Evaluate(input, new[] { Sustained80For30 });

        result.Alerts.Select(a => a.Series.Device.Value).OrderBy(d => d).ShouldBe(new[] { "PUMP-01", "PUMP-02" });
    }

    [Fact]
    public void Rule_evaluations_count_one_per_sustained_rule_per_series_plus_stateless_readings()
    {
        var input = EpisodeSeries("PUMP-01").Concat(EpisodeSeries("PUMP-02")).ToList();
        var rules = new[]
        {
            Sustained80For30,
            TestRules.Stateless("R1", "GreaterThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 1000) }),
        };

        var result = _evaluator.Evaluate(input, rules);

        result.RuleEvaluations.ShouldBe(2 + (9 * 2)); // 2 series sustained scans + 18 per-reading evaluations
    }

    [Fact]
    public void Readings_with_equal_timestamps_are_ordered_by_seq_before_scanning()
    {
        // At 08:00:20 two readings tie: seq 3 is 70 (not above), seq 4 is 90 (above). By seq the episode starts at that
        // timestamp and lasts to 08:00:50. Ordered the other way round it would be cut short.
        var tie = new[]
        {
            Readings.Temp("PUMP-01", "2025-06-01T08:00:10Z", 2, 70),
            Readings.Temp("PUMP-01", "2025-06-01T08:00:20Z", 4, 90),
            Readings.Temp("PUMP-01", "2025-06-01T08:00:20Z", 3, 70),
            Readings.Temp("PUMP-01", "2025-06-01T08:00:30Z", 5, 90),
            Readings.Temp("PUMP-01", "2025-06-01T08:00:40Z", 6, 90),
            Readings.Temp("PUMP-01", "2025-06-01T08:00:50Z", 7, 70),
        };

        var result = _evaluator.Evaluate(Readings.Shuffled(tie), new[] { Sustained80For30 });

        var alert = result.Alerts.Single();
        alert.StartTs.ShouldBe(Readings.At("2025-06-01T08:00:20Z"));
        alert.EndTs.ShouldBe(Readings.At("2025-06-01T08:00:50Z"));
    }

    [Fact]
    public void An_episode_open_at_the_end_of_data_is_flagged_on_the_alert()
    {
        var series = Readings.Series("PUMP-01", Metric.Temperature, "2025-06-01T08:00:00Z", 10, 70, 90, 91, 92, 93);

        var alert = _evaluator.Evaluate(series, new[] { Sustained80For30 }).Alerts.Single();

        alert.OpenAtEndOfData.ShouldBeTrue();
        alert.EndTs.ShouldBe(Readings.At("2025-06-01T08:00:40Z"));
    }

    [Fact]
    public void A_far_future_reading_does_not_break_the_scan()
    {
        var series = Readings.Series("PUMP-01", Metric.Temperature, "2025-06-01T08:00:00Z", 10, 90, 90, 90, 70)
            .Append(Readings.Temp("PUMP-01", "2099-01-01T00:00:00Z", 99, 70))
            .ToList();

        _evaluator.Evaluate(series, new[] { Sustained80For30 }).Alerts.Count.ShouldBe(1);
    }

    [Fact]
    public void A_series_with_a_single_reading_is_evaluated_without_error()
    {
        var result = _evaluator.Evaluate(new[] { Readings.Temp("PUMP-01", "2025-06-01T08:00:00Z", 1, 90) }, new[] { Sustained80For30 });

        result.Alerts.ShouldBeEmpty();
        result.Classifications.Single().Classification.ShouldBe(Classification.Acceptable);
    }
}
