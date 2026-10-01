using System;
using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Aggregation;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Tests.Support;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Aggregation;

public sealed class AggregationCalculatorTests
{
    private static readonly DateTimeOffset From = Readings.At("2025-06-01T08:00:00Z");
    private readonly AggregationCalculator _calculator = new();

    private static Reading At(int second, double value, long seq = 1) =>
        new(new ReadingKey(new DeviceId("D1"), Metric.Temperature, From.AddSeconds(second), seq), value);

    private IReadOnlyList<AggregationBucket> Run(int toSecond, int bucketSeconds, params Reading[] readings) =>
        _calculator.Calculate(readings, From, From.AddSeconds(toSecond), TimeSpan.FromSeconds(bucketSeconds));

    [Fact]
    public void Count_average_min_and_max_match_a_hand_computed_oracle()
    {
        var buckets = Run(120, 60, At(0, 70, 1), At(10, 72, 2), At(50, 74, 3), At(60, 80, 4), At(90, 82, 5));

        buckets.Count.ShouldBe(2);
        buckets[0].Start.ShouldBe(From);
        buckets[0].Count.ShouldBe(3);
        buckets[0].Average.ShouldBe(72.0);
        buckets[0].Min.ShouldBe(70);
        buckets[0].Max.ShouldBe(74);
        buckets[1].Start.ShouldBe(From.AddSeconds(60));
        buckets[1].Count.ShouldBe(2);
        buckets[1].Average.ShouldBe(81.0);
        buckets[1].Min.ShouldBe(80);
        buckets[1].Max.ShouldBe(82);
    }

    [Fact]
    public void A_reading_exactly_on_a_bucket_start_belongs_to_the_bucket_starting_at_that_instant()
    {
        var buckets = Run(120, 60, At(59, 1, 1), At(60, 2, 2));

        buckets[0].Count.ShouldBe(1);
        buckets[0].Max.ShouldBe(1);
        buckets[1].Count.ShouldBe(1);
        buckets[1].Min.ShouldBe(2);
    }

    [Fact]
    public void The_interval_is_half_open_from_is_included_and_to_is_excluded()
    {
        var buckets = Run(60, 60, At(0, 5, 1), At(60, 99, 2), At(-1, 99, 3));

        buckets.Single().Count.ShouldBe(1);
        buckets.Single().Max.ShouldBe(5);
    }

    [Fact]
    public void Empty_buckets_are_reported_with_count_zero_and_null_statistics()
    {
        var buckets = Run(120, 30, At(0, 70, 1), At(100, 80, 2));

        buckets.Select(b => b.Count).ShouldBe(new[] { 1, 0, 0, 1 });
        buckets[1].Average.ShouldBeNull();
        buckets[1].Min.ShouldBeNull();
        buckets[1].Max.ShouldBeNull();
        buckets[1].Start.ShouldBe(From.AddSeconds(30));
    }

    [Fact]
    public void Buckets_are_aligned_to_from_not_to_the_epoch()
    {
        var odd = From.AddSeconds(7);

        var buckets = _calculator.Calculate(
            new[] { At(7, 1, 1), At(66, 2, 2), At(67, 3, 3) }, odd, odd.AddSeconds(120), TimeSpan.FromSeconds(60));

        buckets[0].Start.ShouldBe(odd);
        buckets[1].Start.ShouldBe(odd.AddSeconds(60));
        buckets[0].Count.ShouldBe(2); // seconds 7 and 66 (before odd + 60 = 67)
        buckets[1].Count.ShouldBe(1); // second 67 exactly at the boundary
    }

    [Fact]
    public void The_last_bucket_may_extend_past_to_but_readings_at_or_after_to_are_excluded()
    {
        var buckets = Run(90, 60, At(10, 1, 1), At(70, 2, 2), At(89, 3, 3), At(90, 4, 4), At(100, 5, 5));

        buckets.Count.ShouldBe(2); // ceil(90 / 60)
        buckets[1].Count.ShouldBe(2); // seconds 70 and 89 only
        buckets[1].Max.ShouldBe(3);
    }

    [Fact]
    public void No_readings_gives_all_empty_buckets()
    {
        var buckets = Run(180, 60);

        buckets.Count.ShouldBe(3);
        buckets.All(b => b.Count == 0 && b.Average is null).ShouldBeTrue();
    }

    [Fact]
    public void The_average_is_not_rounded()
    {
        var buckets = Run(60, 60, At(0, 1, 1), At(10, 1, 2), At(20, 2, 3));

        buckets.Single().Average.ShouldBe(4.0 / 3.0);
    }

    [Fact]
    public void Only_the_readings_handed_in_are_counted_so_unacceptable_ones_filtered_upstream_never_appear()
    {
        var acceptable = new[] { At(0, 70, 1), At(30, 74, 3) }; // the 150 at second 20 was filtered out by the query

        Run(60, 60, acceptable).Single().Max.ShouldBe(74);
    }

    [Fact]
    public void The_bucket_count_is_the_ceiling_of_range_over_bucket_size()
    {
        _calculator.BucketCount(From, From.AddSeconds(240), TimeSpan.FromSeconds(60)).ShouldBe(4);
        _calculator.BucketCount(From, From.AddSeconds(241), TimeSpan.FromSeconds(60)).ShouldBe(5);
        _calculator.BucketCount(From, From.AddSeconds(1), TimeSpan.FromSeconds(60)).ShouldBe(1);
    }
}
