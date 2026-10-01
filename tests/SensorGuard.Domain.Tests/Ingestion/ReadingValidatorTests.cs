using System;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Domain.Model;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Ingestion;

public sealed class ReadingValidatorTests
{
    private readonly ReadingValidator _validator = new();

    private static RawReading Raw(
        Field? deviceId = null,
        Field? metric = null,
        Field? ts = null,
        Field? value = null,
        Field? seq = null) =>
        new(
            7,
            deviceId ?? Field.Str("PUMP-01"),
            metric ?? Field.Str("temperature"),
            ts ?? Field.Str("2025-06-01T08:33:00Z"),
            value ?? Field.Num(67.21),
            seq ?? Field.Int(1199));

    private void ShouldReject(RawReading raw, RejectionReason reason)
    {
        var result = _validator.Validate(raw);

        result.IsValid.ShouldBeFalse();
        result.Rejection.ShouldNotBeNull();
        result.Rejection.Reason.ShouldBe(reason);
        result.Rejection.LineNumber.ShouldBe(7);
        result.Rejection.Detail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_well_formed_line_becomes_a_reading()
    {
        var result = _validator.Validate(Raw());

        result.IsValid.ShouldBeTrue();
        var reading = result.Reading!;
        reading.Key.Device.ShouldBe(new DeviceId("PUMP-01"));
        reading.Key.Metric.ShouldBe(Metric.Temperature);
        reading.Key.Timestamp.ShouldBe(new DateTimeOffset(2025, 6, 1, 8, 33, 0, TimeSpan.Zero));
        reading.Key.Seq.ShouldBe(1199);
        reading.Value.ShouldBe(67.21);
    }

    [Fact]
    public void Negative_vibration_is_valid_data()
    {
        var result = _validator.Validate(Raw(metric: Field.Str("vibration"), value: Field.Num(-0.629)));

        result.IsValid.ShouldBeTrue();
        result.Reading!.Value.ShouldBe(-0.629);
    }

    [Fact]
    public void Extreme_but_finite_values_are_valid()
    {
        _validator.Validate(Raw(value: Field.Num(1_000_000))).IsValid.ShouldBeTrue();
        _validator.Validate(Raw(value: Field.Num(-9999))).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(FieldKind.Missing)]
    [InlineData(FieldKind.Null)]
    public void Missing_or_null_fields_are_MissingField(FieldKind kind)
    {
        var field = kind == FieldKind.Missing ? Field.Missing : Field.Null;

        ShouldReject(Raw(deviceId: field), RejectionReason.MissingField);
        ShouldReject(Raw(metric: field), RejectionReason.MissingField);
        ShouldReject(Raw(ts: field), RejectionReason.MissingField);
        ShouldReject(Raw(value: field), RejectionReason.MissingField);
        ShouldReject(Raw(seq: field), RejectionReason.MissingField);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_device_id_is_MissingField(string deviceId)
    {
        ShouldReject(Raw(deviceId: Field.Str(deviceId)), RejectionReason.MissingField);
    }

    [Fact]
    public void A_string_value_is_InvalidType_even_when_it_looks_numeric()
    {
        ShouldReject(Raw(value: Field.Str("12")), RejectionReason.InvalidType);
        ShouldReject(Raw(value: Field.Str("NaN")), RejectionReason.InvalidType);
    }

    [Fact]
    public void Wrong_json_types_for_other_fields_are_InvalidType()
    {
        ShouldReject(Raw(deviceId: Field.Num(5)), RejectionReason.InvalidType);
        ShouldReject(Raw(metric: Field.Other), RejectionReason.InvalidType);
        ShouldReject(Raw(ts: Field.Num(1748766780)), RejectionReason.InvalidType);
        ShouldReject(Raw(seq: Field.Str("12")), RejectionReason.InvalidType);
        ShouldReject(Raw(value: Field.Other), RejectionReason.InvalidType);
    }

    [Theory]
    [InlineData("Temperature")]
    [InlineData("VIBRATION")]
    [InlineData("humidity")]
    public void Unknown_or_wrong_case_metric_is_UnknownMetric(string metric)
    {
        ShouldReject(Raw(metric: Field.Str(metric)), RejectionReason.UnknownMetric);
    }

    [Theory]
    [InlineData("2025-06-01T08:00:05")]
    [InlineData("2025-06-31T08:04:10Z")]
    [InlineData("not a timestamp")]
    [InlineData("2025-06-01")]
    [InlineData("2025-06-01 08:00:00Z")]
    [InlineData("")]
    public void Unparseable_or_offsetless_timestamps_are_InvalidTimestamp(string ts)
    {
        ShouldReject(Raw(ts: Field.Str(ts)), RejectionReason.InvalidTimestamp);
    }

    [Theory]
    [InlineData("2025-06-01T08:33:00Z", 8, 33, 0, 0)]
    [InlineData("2025-06-01T10:33:00+02:00", 8, 33, 0, 0)]
    [InlineData("2025-06-01T06:03:00-02:30", 8, 33, 0, 0)]
    [InlineData("2025-06-01T08:33:00.5Z", 8, 33, 0, 500)]
    [InlineData("2025-06-01T08:33:00.1234567Z", 8, 33, 0, 123)]
    public void Timestamps_with_z_or_offset_and_optional_fraction_are_normalized_to_utc(
        string ts, int hour, int minute, int second, int millisecond)
    {
        var result = _validator.Validate(Raw(ts: Field.Str(ts)));

        result.IsValid.ShouldBeTrue();
        var utc = result.Reading!.Key.Timestamp;
        utc.Offset.ShouldBe(TimeSpan.Zero);
        utc.Hour.ShouldBe(hour);
        utc.Minute.ShouldBe(minute);
        utc.Second.ShouldBe(second);
        utc.Millisecond.ShouldBe(millisecond);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void Non_finite_numbers_are_NonFiniteValue(double value)
    {
        ShouldReject(Raw(value: Field.Num(value)), RejectionReason.NonFiniteValue);
    }

    [Fact]
    public void Negative_seq_is_InvalidSeq()
    {
        ShouldReject(Raw(seq: Field.Int(-1)), RejectionReason.InvalidSeq);
    }

    [Fact]
    public void Seq_with_a_fraction_or_exponent_is_InvalidSeq_even_when_whole()
    {
        ShouldReject(Raw(seq: Field.Num(1.5)), RejectionReason.InvalidSeq);
        ShouldReject(Raw(seq: Field.Num(5.0)), RejectionReason.InvalidSeq);
    }

    [Fact]
    public void Seq_above_the_64_bit_range_is_InvalidSeq()
    {
        ShouldReject(Raw(seq: Field.OversizedInteger(1.8e19)), RejectionReason.InvalidSeq);
    }

    [Fact]
    public void Zero_seq_is_valid()
    {
        _validator.Validate(Raw(seq: Field.Int(0))).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void When_several_defects_exist_the_precedence_in_research_R2_applies()
    {
        // Missing beats type, type beats unknown metric, unknown metric beats timestamp,
        // timestamp beats non-finite, non-finite beats seq.
        ShouldReject(Raw(deviceId: Field.Missing, metric: Field.Str("x"), seq: Field.Int(-1)), RejectionReason.MissingField);
        ShouldReject(Raw(value: Field.Str("1"), metric: Field.Str("x")), RejectionReason.InvalidType);
        ShouldReject(Raw(metric: Field.Str("x"), ts: Field.Str("bad")), RejectionReason.UnknownMetric);
        ShouldReject(Raw(ts: Field.Str("bad"), value: Field.Num(double.PositiveInfinity)), RejectionReason.InvalidTimestamp);
        ShouldReject(Raw(value: Field.Num(double.PositiveInfinity), seq: Field.Int(-1)), RejectionReason.NonFiniteValue);
    }
}
