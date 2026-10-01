using System;
using SensorGuard.Domain.Model;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Model;

public sealed class ModelTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DeviceId_rejects_empty_and_whitespace(string value)
    {
        Should.Throw<ArgumentException>(() => new DeviceId(value));
    }

    [Fact]
    public void DeviceId_is_case_sensitive()
    {
        new DeviceId("PUMP-01").ShouldNotBe(new DeviceId("pump-01"));
    }

    [Theory]
    [InlineData("temperature", Metric.Temperature)]
    [InlineData("pressure", Metric.Pressure)]
    [InlineData("vibration", Metric.Vibration)]
    public void Metric_parses_lowercase_names(string text, Metric expected)
    {
        MetricNames.TryParse(text, out var metric).ShouldBeTrue();
        metric.ShouldBe(expected);
        MetricNames.Format(expected).ShouldBe(text);
    }

    [Theory]
    [InlineData("Temperature")]
    [InlineData("VIBRATION")]
    [InlineData("humidity")]
    [InlineData("")]
    public void Metric_parsing_is_case_sensitive_and_closed(string text)
    {
        MetricNames.TryParse(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void ReadingKey_normalizes_timestamp_to_utc()
    {
        var local = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.FromHours(2));

        var key = new ReadingKey(new DeviceId("d"), Metric.Pressure, local, 1);

        key.Timestamp.Offset.ShouldBe(TimeSpan.Zero);
        key.Timestamp.UtcDateTime.Hour.ShouldBe(8);
    }

    [Fact]
    public void ReadingKey_equal_instants_with_different_offsets_are_equal()
    {
        var a = new ReadingKey(new DeviceId("d"), Metric.Pressure, new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.FromHours(2)), 1);
        var b = new ReadingKey(new DeviceId("d"), Metric.Pressure, new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero), 1);

        a.ShouldBe(b);
    }

    [Fact]
    public void ReadingKey_rejects_negative_seq()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new ReadingKey(new DeviceId("d"), Metric.Pressure, DateTimeOffset.UnixEpoch, -1));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Reading_rejects_non_finite_values(double value)
    {
        var key = new ReadingKey(new DeviceId("d"), Metric.Pressure, DateTimeOffset.UnixEpoch, 1);

        Should.Throw<ArgumentOutOfRangeException>(() => new Reading(key, value));
    }

    [Fact]
    public void Reading_exposes_its_series()
    {
        var key = new ReadingKey(new DeviceId("d"), Metric.Vibration, DateTimeOffset.UnixEpoch, 1);

        new Reading(key, -0.629).Series.ShouldBe(new SeriesKey(new DeviceId("d"), Metric.Vibration));
    }
}
