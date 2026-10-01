using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Rules.Operators;
using SensorGuard.Domain.Tests.Support;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Rules;

public sealed class SustainedAboveTests
{
    private const string Start = "2025-06-01T08:00:00Z";
    private readonly SustainedAboveOperator _op = new();

    private static List<Reading> Seq(params (int Second, double Value)[] points) =>
        points.Select((p, i) => Readings.Temp("PUMP-01", Readings.At(Start).AddSeconds(p.Second).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), i + 1, p.Value)).ToList();

    private static IReadOnlyList<SensorGuard.Domain.Evaluation.Episode> Run(double threshold, double duration, params (int Second, double Value)[] points) =>
        new SustainedAboveOperator().Evaluate(TestRules.Sustained("S", Metric.Temperature, threshold, duration), Seq(points));

    [Fact]
    public void Name_is_SustainedAbove()
    {
        _op.Name.ShouldBe("SustainedAbove");
    }

    [Fact]
    public void An_episode_lasting_exactly_the_duration_is_sustained()
    {
        // above at 0,10,20; first reading at or below the threshold at 30 => duration 30 == durationSeconds
        var episodes = Run(80, 30, (0, 90), (10, 91), (20, 92), (30, 70));

        var episode = episodes.Single();
        episode.StartTs.ShouldBe(Readings.At(Start));
        episode.EndTs.ShouldBe(Readings.At(Start).AddSeconds(30));
        episode.OpenAtEnd.ShouldBeFalse();
    }

    [Fact]
    public void An_episode_one_second_short_of_the_duration_is_not_sustained()
    {
        Run(80, 30, (0, 90), (10, 91), (29, 70)).ShouldBeEmpty();
    }

    [Fact]
    public void A_value_equal_to_the_threshold_is_not_above_and_ends_the_episode()
    {
        var episodes = Run(80, 20, (0, 90), (10, 91), (20, 80), (30, 95));

        var episode = episodes.Single();
        episode.EndTs.ShouldBe(Readings.At(Start).AddSeconds(20));
        episode.Covered.Select(k => k.Seq).ShouldBe(new long[] { 1, 2 });
    }

    [Fact]
    public void A_value_equal_to_the_threshold_never_starts_an_episode()
    {
        Run(80, 10, (0, 80), (10, 80), (20, 80), (30, 80)).ShouldBeEmpty();
    }

    [Fact]
    public void A_single_above_reading_followed_by_a_non_above_one_lasts_the_gap_between_them()
    {
        var episode = Run(80, 30, (0, 90), (30, 70)).Single();

        (episode.EndTs - episode.StartTs).ShouldBe(TimeSpan.FromSeconds(30));
        episode.Covered.Count.ShouldBe(1);
    }

    [Fact]
    public void An_episode_still_open_at_the_end_of_data_ends_at_the_last_reading_and_is_flagged()
    {
        var episode = Run(80, 30, (0, 90), (10, 91), (20, 92), (30, 93)).Single();

        episode.OpenAtEnd.ShouldBeTrue();
        episode.EndTs.ShouldBe(Readings.At(Start).AddSeconds(30));
        episode.Covered.Count.ShouldBe(4);
    }

    [Fact]
    public void An_open_episode_shorter_than_the_duration_is_not_sustained()
    {
        Run(80, 31, (0, 90), (10, 91), (20, 92), (30, 93)).ShouldBeEmpty();
    }

    [Fact]
    public void A_single_reading_series_above_threshold_has_zero_duration_and_is_not_sustained()
    {
        Run(80, 30, (0, 90)).ShouldBeEmpty();
    }

    [Fact]
    public void An_episode_can_start_at_the_first_reading_of_the_series()
    {
        Run(80, 20, (0, 90), (10, 90), (20, 70)).Single().StartTs.ShouldBe(Readings.At(Start));
    }

    [Fact]
    public void A_gap_in_the_data_does_not_split_an_episode_the_last_value_is_held()
    {
        var episode = Run(80, 60, (0, 90), (300, 91), (310, 70)).Single();

        episode.StartTs.ShouldBe(Readings.At(Start));
        episode.EndTs.ShouldBe(Readings.At(Start).AddSeconds(310));
        episode.Covered.Count.ShouldBe(2);
    }

    [Fact]
    public void Peak_is_the_highest_value_in_the_episode()
    {
        Run(80, 20, (0, 85), (10, 99.5), (20, 90), (30, 70)).Single().Peak.ShouldBe(99.5);
    }

    [Fact]
    public void Two_separate_episodes_in_one_series_are_both_returned()
    {
        var episodes = Run(80, 20, (0, 90), (10, 90), (20, 70), (30, 90), (40, 90), (50, 90), (60, 70));

        episodes.Count.ShouldBe(2);
        episodes[0].StartTs.ShouldBe(Readings.At(Start));
        episodes[1].StartTs.ShouldBe(Readings.At(Start).AddSeconds(30));
    }

    [Fact]
    public void An_above_run_shorter_than_the_duration_returns_nothing_even_between_sustained_ones()
    {
        var episodes = Run(80, 30, (0, 90), (10, 70), (20, 90), (30, 90), (40, 90), (50, 70));

        episodes.Single().StartTs.ShouldBe(Readings.At(Start).AddSeconds(20));
    }

    [Fact]
    public void Covered_readings_are_those_from_the_start_up_to_but_excluding_the_ending_reading()
    {
        var episode = Run(80, 20, (0, 70), (10, 90), (20, 90), (30, 90), (40, 70)).Single();

        episode.Covered.Select(k => k.Seq).ShouldBe(new long[] { 2, 3, 4 });
    }

    [Fact]
    public void Validation_requires_a_finite_threshold_and_a_positive_finite_duration()
    {
        _op.Validate(TestRules.Params(("threshold", 80), ("durationSeconds", 30))).ShouldBeEmpty();
        _op.Validate(TestRules.Params(("durationSeconds", 30))).ShouldNotBeEmpty();
        _op.Validate(TestRules.Params(("threshold", 80))).ShouldNotBeEmpty();
        _op.Validate(TestRules.Params(("threshold", double.NaN), ("durationSeconds", 30))).ShouldNotBeEmpty();
        _op.Validate(TestRules.Params(("threshold", 80), ("durationSeconds", 0))).ShouldNotBeEmpty();
        _op.Validate(TestRules.Params(("threshold", 80), ("durationSeconds", -5))).ShouldNotBeEmpty();
        _op.Validate(TestRules.Params(("threshold", 80), ("durationSeconds", double.PositiveInfinity))).ShouldNotBeEmpty();
    }
}
