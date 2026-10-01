using System;

namespace SensorGuard.Domain.Model;

/// <summary>Opaque device identifier. Non-empty, compared ordinally (case-sensitive).</summary>
public readonly record struct DeviceId
{
    public DeviceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Device id must not be empty or whitespace.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
