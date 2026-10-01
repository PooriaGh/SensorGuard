using System;

namespace SensorGuard.Domain.Model;

/// <summary>A valid sensor reading. Plausibility is not validated: rules decide acceptability.</summary>
public sealed record Reading
{
    public Reading(ReadingKey key, double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Reading value must be finite.");
        }

        Key = key;
        Value = value;
    }

    public ReadingKey Key { get; }

    public double Value { get; }

    public SeriesKey Series => Key.Series;
}
