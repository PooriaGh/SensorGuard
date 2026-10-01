using System;
using System.Collections.Generic;
using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Aggregation;

/// <summary>One time bucket. Average, min and max are null exactly when <see cref="Count"/> is 0.</summary>
public sealed record AggregationBucket(DateTimeOffset Start, int Count, double? Average, double? Min, double? Max);

/// <summary>
/// Pure bucketing over the readings it is given (callers pass acceptable readings only). Buckets are aligned to
/// <c>from</c>, cover the half-open interval [from, to), and every bucket in the range is reported, empty or not.
/// </summary>
public sealed class AggregationCalculator
{
    /// <summary>ceil((to - from) / bucket): the last bucket may extend past <paramref name="to"/>.</summary>
    public long BucketCount(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket)
    {
        var range = (to - from).Ticks;
        return (range + bucket.Ticks - 1) / bucket.Ticks;
    }

    public IReadOnlyList<AggregationBucket> Calculate(
        IEnumerable<Reading> readings, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket)
    {
        var count = checked((int)BucketCount(from, to, bucket));
        var counts = new int[count];
        var sums = new double[count];
        var mins = new double[count];
        var maxes = new double[count];

        foreach (var reading in readings)
        {
            var timestamp = reading.Key.Timestamp;
            if (timestamp < from || timestamp >= to)
            {
                continue;
            }

            var index = (int)((timestamp - from).Ticks / bucket.Ticks);
            if (counts[index] == 0)
            {
                mins[index] = reading.Value;
                maxes[index] = reading.Value;
            }
            else
            {
                mins[index] = Math.Min(mins[index], reading.Value);
                maxes[index] = Math.Max(maxes[index], reading.Value);
            }

            counts[index]++;
            sums[index] += reading.Value;
        }

        var buckets = new List<AggregationBucket>(count);
        for (var i = 0; i < count; i++)
        {
            var start = from + TimeSpan.FromTicks(bucket.Ticks * i);
            buckets.Add(counts[i] == 0
                ? new AggregationBucket(start, 0, null, null, null)
                : new AggregationBucket(start, counts[i], sums[i] / counts[i], mins[i], maxes[i]));
        }

        return buckets;
    }
}
