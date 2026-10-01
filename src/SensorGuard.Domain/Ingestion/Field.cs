namespace SensorGuard.Domain.Ingestion;

public enum FieldKind
{
    Missing,
    Null,
    String,
    Number,
    Other,
}

/// <summary>
/// A JSON-neutral view of one input field, so Domain can validate types without any serialization
/// dependency (research R1). <see cref="IsIntegerToken"/> is true only for tokens without a fraction
/// or exponent; <see cref="Integer"/> is set only when such a token fits a 64-bit signed integer.
/// </summary>
public readonly record struct Field(
    FieldKind Kind,
    string? Text = null,
    double? Number = null,
    long? Integer = null,
    bool IsIntegerToken = false)
{
    public static Field Missing => new(FieldKind.Missing);

    public static Field Null => new(FieldKind.Null);

    public static Field Other => new(FieldKind.Other);

    public static Field Str(string text) => new(FieldKind.String, Text: text);

    /// <summary>A number token written with a fraction or exponent (or any non-integer value).</summary>
    public static Field Num(double value) => new(FieldKind.Number, Number: value);

    /// <summary>A plain integer token that fits a 64-bit signed integer.</summary>
    public static Field Int(long value) => new(FieldKind.Number, Number: value, Integer: value, IsIntegerToken: true);

    /// <summary>A plain integer token too large for a 64-bit signed integer.</summary>
    public static Field OversizedInteger(double approximateValue) =>
        new(FieldKind.Number, Number: approximateValue, IsIntegerToken: true);
}
