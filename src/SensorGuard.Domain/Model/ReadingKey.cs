using System;

namespace SensorGuard.Domain.Model;

/// <summary>Identity of a reading: (deviceId, metric, ts, seq). Timestamp is normalized to UTC.</summary>
public sealed record ReadingKey
{
    public ReadingKey(DeviceId device, Metric metric, DateTimeOffset timestamp, long seq)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seq);
        Device = device;
        Metric = metric;
        Timestamp = timestamp.ToUniversalTime();
        Seq = seq;
    }

    public DeviceId Device { get; }

    public Metric Metric { get; }

    public DateTimeOffset Timestamp { get; }

    public long Seq { get; }

    public SeriesKey Series => new(Device, Metric);
}
