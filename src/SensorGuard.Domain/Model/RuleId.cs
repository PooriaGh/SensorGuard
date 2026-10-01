using System;

namespace SensorGuard.Domain.Model;

public readonly record struct RuleId
{
    public RuleId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Rule id must not be empty or whitespace.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
