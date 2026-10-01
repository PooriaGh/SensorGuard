using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Tests.Support;

/// <summary>Tiny deterministic builders shared by all domain tests (Principle VII: no clock, fixed seeds).</summary>
internal static class Readings
{
    public const int FixedShuffleSeed = 20251;

    public static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    public static Reading Make(string device, Metric metric, string ts, long seq, double value) =>
        new(new ReadingKey(new DeviceId(device), metric, At(ts), seq), value);

    public static Reading Temp(string device, string ts, long seq, double value) =>
        Make(device, Metric.Temperature, ts, seq, value);

    public static Reading Pressure(string device, string ts, long seq, double value) =>
        Make(device, Metric.Pressure, ts, seq, value);

    public static Reading Vibration(string device, string ts, long seq, double value) =>
        Make(device, Metric.Vibration, ts, seq, value);

    /// <summary>One reading every <paramref name="stepSeconds"/> seconds from <paramref name="start"/>.</summary>
    public static List<Reading> Series(string device, Metric metric, string start, int stepSeconds, params double[] values)
    {
        var t0 = At(start);
        return values
            .Select((v, i) => new Reading(new ReadingKey(new DeviceId(device), metric, t0.AddSeconds(i * stepSeconds), i + 1), v))
            .ToList();
    }

    public static List<T> Shuffled<T>(IEnumerable<T> items, int seed = FixedShuffleSeed)
    {
        var list = items.ToList();
        var rng = new Random(seed);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
