using System;

namespace SensorGuard.Application;

/// <summary>Bound from the <c>SensorGuard</c> configuration section.</summary>
public sealed class SensorGuardOptions
{
    public const string SectionName = "SensorGuard";

    public static readonly TimeSpan DefaultAlertCooldown = TimeSpan.FromMinutes(5);

    public const int DefaultMaxAggregationBuckets = 10_000;

    public string InputFilePath { get; set; } = "data/sensor-data.json";

    public string RulesFilePath { get; set; } = "data/rules.json";

    public string DatabasePath { get; set; } = "sensorguard.db";

    public TimeSpan AlertCooldown { get; set; } = DefaultAlertCooldown;

    public int MaxAggregationBuckets { get; set; } = DefaultMaxAggregationBuckets;
}
