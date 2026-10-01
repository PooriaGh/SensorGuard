using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Tests.Support;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Ingestion;

public sealed class DeduplicatorTests
{
    private readonly Deduplicator _dedup = new();

    private static IEnumerable<IReadOnlyList<T>> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, index) => index != i).ToList();
            foreach (var tail in Permutations(rest))
            {
                yield return new[] { items[i] }.Concat(tail).ToList();
            }
        }
    }

    [Fact]
    public void Identical_copies_collapse_to_one_and_are_counted_as_duplicates()
    {
        var r = Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 70);

        var result = _dedup.Deduplicate(new[] { r, r, r });

        result.Kept.Count.ShouldBe(1);
        result.Duplicates.ShouldBe(2);
        result.ConflictingDuplicates.ShouldBeEmpty();
    }

    [Fact]
    public void Conflicting_copies_keep_the_lowest_value_and_report_the_conflict()
    {
        var a = Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 70);
        var b = Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 65);

        var result = _dedup.Deduplicate(new[] { a, b });

        result.Kept.Single().Value.ShouldBe(65);
        result.Duplicates.ShouldBe(1);
        var conflict = result.ConflictingDuplicates.Single();
        conflict.Key.ShouldBe(a.Key);
        conflict.KeptValue.ShouldBe(65);
        conflict.IgnoredValues.ShouldBe(new[] { 70.0 });
    }

    [Fact]
    public void The_result_is_identical_for_every_permutation_of_the_input()
    {
        var copies = new List<Reading>
        {
            Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 70),
            Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 65),
            Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 68),
            Readings.Temp("d", "2025-06-01T08:00:10Z", 2, 71),
        };

        var results = Permutations(copies).Select(p => _dedup.Deduplicate(p)).ToList();

        results.Count.ShouldBe(24);
        foreach (var result in results)
        {
            result.Kept.Select(r => (r.Key, r.Value)).ShouldBe(results[0].Kept.Select(r => (r.Key, r.Value)));
            result.Duplicates.ShouldBe(2);
            result.ConflictingDuplicates.Single().KeptValue.ShouldBe(65);
            result.ConflictingDuplicates.Single().IgnoredValues.OrderBy(v => v).ShouldBe(new[] { 68.0, 70.0 });
        }
    }

    [Fact]
    public void Same_timestamp_with_different_seq_are_distinct_readings()
    {
        var a = Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 70);
        var b = Readings.Temp("d", "2025-06-01T08:00:00Z", 2, 70);

        var result = _dedup.Deduplicate(new[] { a, b });

        result.Kept.Count.ShouldBe(2);
        result.Duplicates.ShouldBe(0);
    }

    [Fact]
    public void Different_device_or_metric_are_distinct_readings()
    {
        var result = _dedup.Deduplicate(new[]
        {
            Readings.Temp("d1", "2025-06-01T08:00:00Z", 1, 70),
            Readings.Temp("d2", "2025-06-01T08:00:00Z", 1, 70),
            Readings.Pressure("d1", "2025-06-01T08:00:00Z", 1, 70),
        });

        result.Kept.Count.ShouldBe(3);
    }

    [Fact]
    public void Kept_readings_are_returned_in_a_stable_series_then_event_time_order()
    {
        var result = _dedup.Deduplicate(Readings.Shuffled(new[]
        {
            Readings.Temp("b", "2025-06-01T08:00:10Z", 2, 1),
            Readings.Temp("a", "2025-06-01T08:00:20Z", 3, 1),
            Readings.Temp("a", "2025-06-01T08:00:00Z", 1, 1),
            Readings.Pressure("a", "2025-06-01T08:00:00Z", 4, 1),
        }));

        // Order: device ordinal, then metric, then (timestamp, seq).
        result.Kept.Select(r => r.Key.Seq).ShouldBe(new long[] { 1, 3, 4, 2 });
    }
}
