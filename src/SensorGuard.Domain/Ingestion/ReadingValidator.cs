using System;
using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Ingestion;

/// <summary>
/// Turns a <see cref="RawReading"/> into a <see cref="Reading"/> or a <see cref="Rejection"/>. A line with several
/// defects reports the first by this fixed precedence (research R2): MissingField, InvalidType, UnknownMetric,
/// InvalidTimestamp, NonFiniteValue, InvalidSeq. Plausibility of values is deliberately not validated.
/// </summary>
public sealed class ReadingValidator
{
    public ValidationResult Validate(RawReading raw)
    {
        var rejection = FindMissing(raw)
            ?? FindWrongType(raw);
        if (rejection is not null)
        {
            return ValidationResult.Rejected(rejection);
        }

        if (!MetricNames.TryParse(raw.Metric.Text, out var metric))
        {
            return Reject(raw, RejectionReason.UnknownMetric, $"metric '{raw.Metric.Text}' is not one of temperature, pressure, vibration");
        }

        if (!TimestampParser.TryParse(raw.Ts.Text!, out var timestamp))
        {
            return Reject(raw, RejectionReason.InvalidTimestamp, $"ts '{raw.Ts.Text}' is not ISO-8601 with Z or an explicit offset");
        }

        var value = raw.Value.Number!.Value;
        if (!double.IsFinite(value))
        {
            return Reject(raw, RejectionReason.NonFiniteValue, "value is not a finite number");
        }

        if (!raw.Seq.IsIntegerToken || raw.Seq.Integer is null || raw.Seq.Integer < 0)
        {
            return Reject(raw, RejectionReason.InvalidSeq, "seq must be a non-negative integer within the 64-bit range");
        }

        var key = new ReadingKey(new DeviceId(raw.DeviceId.Text!), metric, timestamp, raw.Seq.Integer.Value);
        return ValidationResult.Valid(new Reading(key, value));
    }

    private static Rejection? FindMissing(RawReading raw)
    {
        if (IsAbsent(raw.DeviceId) || (raw.DeviceId.Kind == FieldKind.String && string.IsNullOrWhiteSpace(raw.DeviceId.Text)))
        {
            return Make(raw, RejectionReason.MissingField, "deviceId is missing, null or empty");
        }

        if (IsAbsent(raw.Metric))
        {
            return Make(raw, RejectionReason.MissingField, "metric is missing or null");
        }

        if (IsAbsent(raw.Ts))
        {
            return Make(raw, RejectionReason.MissingField, "ts is missing or null");
        }

        if (IsAbsent(raw.Value))
        {
            return Make(raw, RejectionReason.MissingField, "value is missing or null");
        }

        return IsAbsent(raw.Seq) ? Make(raw, RejectionReason.MissingField, "seq is missing or null") : null;
    }

    private static Rejection? FindWrongType(RawReading raw)
    {
        if (raw.DeviceId.Kind != FieldKind.String)
        {
            return Make(raw, RejectionReason.InvalidType, "deviceId must be a JSON string");
        }

        if (raw.Metric.Kind != FieldKind.String)
        {
            return Make(raw, RejectionReason.InvalidType, "metric must be a JSON string");
        }

        if (raw.Ts.Kind != FieldKind.String)
        {
            return Make(raw, RejectionReason.InvalidType, "ts must be a JSON string");
        }

        if (raw.Value.Kind != FieldKind.Number)
        {
            return Make(raw, RejectionReason.InvalidType, "value must be a JSON number, not a string or other type");
        }

        return raw.Seq.Kind != FieldKind.Number ? Make(raw, RejectionReason.InvalidType, "seq must be a JSON number") : null;
    }

    private static bool IsAbsent(Field field) => field.Kind is FieldKind.Missing or FieldKind.Null;

    private static Rejection Make(RawReading raw, RejectionReason reason, string detail) =>
        new(raw.LineNumber, reason, detail);

    private static ValidationResult Reject(RawReading raw, RejectionReason reason, string detail) =>
        ValidationResult.Rejected(Make(raw, reason, detail));
}
