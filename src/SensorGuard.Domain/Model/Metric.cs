namespace SensorGuard.Domain.Model;

public enum Metric
{
    Temperature,
    Pressure,
    Vibration,
}

/// <summary>Maps metrics to their canonical lowercase wire names (case-sensitive, closed set).</summary>
public static class MetricNames
{
    public static bool TryParse(string? text, out Metric metric)
    {
        switch (text)
        {
            case "temperature":
                metric = Metric.Temperature;
                return true;
            case "pressure":
                metric = Metric.Pressure;
                return true;
            case "vibration":
                metric = Metric.Vibration;
                return true;
            default:
                metric = default;
                return false;
        }
    }

    public static string Format(Metric metric) => metric switch
    {
        Metric.Temperature => "temperature",
        Metric.Pressure => "pressure",
        Metric.Vibration => "vibration",
        _ => throw new System.ArgumentOutOfRangeException(nameof(metric), metric, "Unknown metric."),
    };
}
