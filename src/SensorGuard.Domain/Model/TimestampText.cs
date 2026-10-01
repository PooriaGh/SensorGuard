using System;
using System.Globalization;

namespace SensorGuard.Domain.Model;

/// <summary>Culture-invariant UTC timestamp text for reasons and logs, e.g. 2025-06-01T08:30:00Z (fractions only when present).</summary>
public static class TimestampText
{
    public static string Format(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)
            .Replace(".Z", "Z", StringComparison.Ordinal);
}
