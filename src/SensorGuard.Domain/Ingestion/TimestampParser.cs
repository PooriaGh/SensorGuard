using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SensorGuard.Domain.Ingestion;

/// <summary>
/// Accepts only ISO-8601 date-times that end in <c>Z</c> or an explicit <c>±hh:mm</c> offset, with optional
/// fractional seconds, and normalizes them to UTC. A timestamp without an offset is ambiguous and rejected.
/// </summary>
public static class TimestampParser
{
    private static readonly Regex Shape = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string text, out DateTimeOffset utc)
    {
        utc = default;
        if (!Shape.IsMatch(text))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        utc = parsed.ToUniversalTime();
        return true;
    }
}
