namespace SensorGuard.Domain.Model;

/// <summary>Grouping and state key for stateful evaluation: one series per (device, metric).</summary>
public readonly record struct SeriesKey(DeviceId Device, Metric Metric)
{
    public override string ToString() => $"{Device}/{MetricNames.Format(Metric)}";
}
