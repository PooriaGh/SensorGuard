using System;
using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Ingestion;

/// <summary>Several copies of one key carried different values; the lowest was kept (research R16).</summary>
public sealed record ConflictingDuplicate(ReadingKey Key, double KeptValue, IReadOnlyList<double> IgnoredValues);

public sealed record DedupResult(
    IReadOnlyList<Reading> Kept,
    int Duplicates,
    IReadOnlyList<ConflictingDuplicate> ConflictingDuplicates);

/// <summary>
/// Collapses readings with an equal (deviceId, metric, ts, seq) key. Copies with equal values collapse to one;
/// copies with different values keep the lowest value. The rule uses no input order, so the result is the same
/// for every permutation of the input (Principle III).
/// </summary>
public sealed class Deduplicator
{
    public DedupResult Deduplicate(IEnumerable<Reading> readings)
    {
        var byKey = new Dictionary<ReadingKey, List<double>>();
        foreach (var reading in readings)
        {
            if (!byKey.TryGetValue(reading.Key, out var values))
            {
                values = new List<double>();
                byKey[reading.Key] = values;
            }

            values.Add(reading.Value);
        }

        var kept = new List<Reading>(byKey.Count);
        var conflicts = new List<ConflictingDuplicate>();
        var duplicates = 0;
        foreach (var (key, values) in byKey)
        {
            var lowest = values.Min();
            kept.Add(new Reading(key, lowest));
            duplicates += values.Count - 1;

            var ignored = values.Where(v => v != lowest).OrderBy(v => v).ToList();
            if (ignored.Count > 0)
            {
                conflicts.Add(new ConflictingDuplicate(key, lowest, ignored));
            }
        }

        kept.Sort(CompareStable);
        conflicts.Sort((a, b) => CompareKeys(a.Key, b.Key));
        return new DedupResult(kept, duplicates, conflicts);
    }

    private static int CompareStable(Reading a, Reading b) => CompareKeys(a.Key, b.Key);

    private static int CompareKeys(ReadingKey a, ReadingKey b)
    {
        var byDevice = string.CompareOrdinal(a.Device.Value, b.Device.Value);
        if (byDevice != 0)
        {
            return byDevice;
        }

        var byMetric = a.Metric.CompareTo(b.Metric);
        if (byMetric != 0)
        {
            return byMetric;
        }

        var byTime = a.Timestamp.CompareTo(b.Timestamp);
        return byTime != 0 ? byTime : a.Seq.CompareTo(b.Seq);
    }
}
