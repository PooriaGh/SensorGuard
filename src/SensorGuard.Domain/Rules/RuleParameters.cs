using System.Collections.Generic;
using System.Globalization;

namespace SensorGuard.Domain.Rules;

/// <summary>Named numeric operator parameters (value, min, max, threshold, durationSeconds, ...) read from rule data.</summary>
public sealed class RuleParameters
{
    private readonly IReadOnlyDictionary<string, double> _values;

    public RuleParameters(IReadOnlyDictionary<string, double> values) => _values = values;

    public double Get(string name) =>
        _values.TryGetValue(name, out var value)
            ? value
            : throw new KeyNotFoundException($"Rule parameter '{name}' is not defined.");

    /// <summary>Errors for each listed parameter that is missing or not finite; empty when all are usable.</summary>
    public IReadOnlyList<string> RequireFinite(params string[] names)
    {
        var errors = new List<string>();
        foreach (var name in names)
        {
            if (!_values.TryGetValue(name, out var value))
            {
                errors.Add($"missing required parameter '{name}'");
            }
            else if (!double.IsFinite(value))
            {
                errors.Add($"parameter '{name}' must be a finite number");
            }
        }

        return errors;
    }

    /// <summary>Shortest round-trip, culture-invariant text for a number (100, 103.2, -0.629).</summary>
    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
