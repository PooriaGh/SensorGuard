using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Model;
using Shouldly;
using Xunit;

namespace SensorGuard.Application.Tests;

public sealed class GetAggregationTests
{
    private sealed class StubQuery : IReadingQuery
    {
        public int Calls { get; private set; }

        public List<Reading> Acceptable { get; } = new();

        public Task<IReadOnlyList<Reading>> GetAcceptableAsync(SeriesKey series, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            Calls++;
            IReadOnlyList<Reading> result = Acceptable
                .Where(r => r.Series == series && r.Key.Timestamp >= from && r.Key.Timestamp < to)
                .ToList();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<UnacceptableReading>> GetUnacceptableAsync(SeriesKey? series, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private readonly StubQuery _query = new();

    private GetAggregation UseCase(int maxBuckets = 10_000) =>
        new(_query, Options.Create(new SensorGuardOptions { MaxAggregationBuckets = maxBuckets }));

    private static AggregationQuery Valid(
        string? deviceId = "PUMP-01",
        string? metric = "temperature",
        string? from = "2025-06-01T08:00:00Z",
        string? to = "2025-06-01T08:04:00Z",
        string? bucketSeconds = "60") =>
        new(deviceId, metric, from, to, bucketSeconds);

    private static Reading Temp(int second, double value, long seq) =>
        new(new ReadingKey(new DeviceId("PUMP-01"), Metric.Temperature, new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero).AddSeconds(second), seq), value);

    private async Task<AggregationResult> Run(AggregationQuery query) => await UseCase().ExecuteAsync(query, CancellationToken.None);

    [Fact]
    public async Task A_valid_query_returns_buckets_covering_the_interval()
    {
        _query.Acceptable.AddRange(new[] { Temp(0, 70, 1), Temp(70, 80, 2) });

        var result = await Run(Valid());

        result.IsValid.ShouldBeTrue();
        result.Buckets.Count.ShouldBe(4);
        result.Buckets.Select(b => b.Count).ShouldBe(new[] { 1, 1, 0, 0 });
        result.Query!.BucketSeconds.ShouldBe(60);
        result.Query.Series.ShouldBe(new SeriesKey(new DeviceId("PUMP-01"), Metric.Temperature));
    }

    [Fact]
    public async Task An_unknown_device_with_a_valid_metric_gives_all_empty_buckets_not_an_error()
    {
        var result = await Run(Valid(deviceId: "NO-SUCH-DEVICE"));

        result.IsValid.ShouldBeTrue();
        result.Buckets.All(b => b.Count == 0).ShouldBeTrue();
    }

    [Theory]
    [InlineData("deviceId")]
    [InlineData("metric")]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("bucketSeconds")]
    public async Task A_missing_parameter_is_reported_by_name(string missing)
    {
        var query = missing switch
        {
            "deviceId" => Valid(deviceId: null),
            "metric" => Valid(metric: null),
            "from" => Valid(from: null),
            "to" => Valid(to: null),
            _ => Valid(bucketSeconds: null),
        };

        var result = await Run(query);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContainKey(missing);
        _query.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_device_id_is_invalid(string deviceId)
    {
        (await Run(Valid(deviceId: deviceId))).Errors.ShouldContainKey("deviceId");
    }

    [Theory]
    [InlineData("humidity")]
    [InlineData("Temperature")]
    public async Task An_unknown_metric_is_invalid(string metric)
    {
        var result = await Run(Valid(metric: metric));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContainKey("metric");
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("2025-06-01T08:00:00")]
    [InlineData("2025-06-01")]
    public async Task Unparseable_or_offsetless_timestamps_are_invalid(string value)
    {
        (await Run(Valid(from: value))).Errors.ShouldContainKey("from");
        (await Run(Valid(to: value))).Errors.ShouldContainKey("to");
    }

    [Fact]
    public async Task Timestamps_with_an_explicit_offset_are_accepted_and_normalized()
    {
        var result = await Run(Valid(from: "2025-06-01T10:00:00+02:00", to: "2025-06-01T08:04:00Z"));

        result.IsValid.ShouldBeTrue();
        result.Query!.From.ShouldBe(new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task From_must_be_earlier_than_to()
    {
        (await Run(Valid(from: "2025-06-01T08:04:00Z", to: "2025-06-01T08:00:00Z"))).Errors.ShouldContainKey("from");
        (await Run(Valid(from: "2025-06-01T08:00:00Z", to: "2025-06-01T08:00:00Z"))).Errors.ShouldContainKey("from");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("99999999999999999999")]
    public async Task A_non_positive_or_unparseable_bucket_size_is_invalid(string bucketSeconds)
    {
        var result = await Run(Valid(bucketSeconds: bucketSeconds));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContainKey("bucketSeconds");
    }

    [Fact]
    public async Task A_bucket_size_too_large_to_represent_is_invalid_not_an_overflow()
    {
        var result = await Run(Valid(bucketSeconds: long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        result.Errors.ShouldContainKey("bucketSeconds");
    }

    [Fact]
    public async Task More_buckets_than_the_configured_maximum_is_invalid_and_is_checked_before_any_query()
    {
        var result = await UseCase(maxBuckets: 3).ExecuteAsync(Valid(), CancellationToken.None); // 4 buckets requested

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContainKey("bucketSeconds");
        _query.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Exactly_the_maximum_number_of_buckets_is_allowed()
    {
        var result = await UseCase(maxBuckets: 4).ExecuteAsync(Valid(), CancellationToken.None);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Several_problems_are_reported_together()
    {
        var result = await Run(Valid(deviceId: null, metric: "x", bucketSeconds: "0"));

        result.Errors.Keys.OrderBy(k => k).ShouldBe(new[] { "bucketSeconds", "deviceId", "metric" });
    }

    [Fact]
    public async Task Unacceptable_readings_are_excluded_because_only_acceptable_ones_are_queried()
    {
        _query.Acceptable.Add(Temp(10, 70, 1)); // the query port returns acceptable readings only

        var result = await Run(Valid());

        result.Buckets[0].Count.ShouldBe(1);
    }
}
