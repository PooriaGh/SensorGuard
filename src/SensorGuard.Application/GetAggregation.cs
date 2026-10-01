using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Aggregation;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Domain.Model;

namespace SensorGuard.Application;

/// <summary>The query exactly as received (all text), so validation problems can be reported per parameter.</summary>
public sealed record AggregationQuery(string? DeviceId, string? Metric, string? From, string? To, string? BucketSeconds);

public sealed record ValidatedAggregationQuery(SeriesKey Series, DateTimeOffset From, DateTimeOffset To, long BucketSeconds);

/// <summary>Either buckets or per-parameter errors; invalid input is a result, never an exception.</summary>
public sealed record AggregationResult(
    ValidatedAggregationQuery? Query,
    IReadOnlyList<AggregationBucket> Buckets,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static AggregationResult Ok(ValidatedAggregationQuery query, IReadOnlyList<AggregationBucket> buckets) =>
        new(query, buckets, new Dictionary<string, string[]>());

    public static AggregationResult Invalid(IReadOnlyDictionary<string, string[]> errors) =>
        new(null, Array.Empty<AggregationBucket>(), errors);
}

/// <summary>Validates the request, reads acceptable readings only, and buckets them (FR-014).</summary>
public sealed class GetAggregation
{
    private static readonly long MaxBucketSeconds = long.MaxValue / TimeSpan.TicksPerSecond;

    private readonly IReadingQuery _query;
    private readonly int _maxBuckets;
    private readonly AggregationCalculator _calculator = new();

    public GetAggregation(IReadingQuery query, IOptions<SensorGuardOptions> options)
    {
        _query = query;
        _maxBuckets = options.Value.MaxAggregationBuckets;
    }

    public async Task<AggregationResult> ExecuteAsync(AggregationQuery request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        DeviceId? device = string.IsNullOrWhiteSpace(request.DeviceId) ? null : new DeviceId(request.DeviceId);
        if (device is null)
        {
            errors["deviceId"] = new[] { "deviceId is required." };
        }

        Metric metric = default;
        if (string.IsNullOrWhiteSpace(request.Metric))
        {
            errors["metric"] = new[] { "metric is required (temperature, pressure or vibration)." };
        }
        else if (!MetricNames.TryParse(request.Metric, out metric))
        {
            errors["metric"] = new[] { $"metric '{request.Metric}' is not one of temperature, pressure, vibration." };
        }

        var from = ParseTimestamp("from", request.From, errors);
        var to = ParseTimestamp("to", request.To, errors);
        if (from is not null && to is not null && from >= to)
        {
            errors["from"] = new[] { "from must be earlier than to." };
        }

        var bucketSeconds = ParseBucketSeconds(request.BucketSeconds, errors);

        TimeSpan bucket = default;
        if (bucketSeconds is not null && from is not null && to is not null && !errors.ContainsKey("from"))
        {
            bucket = TimeSpan.FromSeconds(bucketSeconds.Value);
            var count = _calculator.BucketCount(from.Value, to.Value, bucket);
            if (count > _maxBuckets)
            {
                errors["bucketSeconds"] = new[]
                {
                    $"The request would produce {count} buckets; the maximum is {_maxBuckets}. Use a larger bucketSeconds or a shorter range.",
                };
            }
        }

        if (errors.Count > 0)
        {
            return AggregationResult.Invalid(errors);
        }

        var validated = new ValidatedAggregationQuery(new SeriesKey(device!.Value, metric), from!.Value, to!.Value, bucketSeconds!.Value);
        var readings = await _query.GetAcceptableAsync(validated.Series, validated.From, validated.To, cancellationToken);
        return AggregationResult.Ok(validated, _calculator.Calculate(readings, validated.From, validated.To, bucket));
    }

    private static DateTimeOffset? ParseTimestamp(string name, string? text, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            errors[name] = new[] { $"{name} is required." };
            return null;
        }

        if (!TimestampParser.TryParse(text, out var parsed))
        {
            errors[name] = new[] { $"{name} '{text}' must be an ISO-8601 timestamp with Z or an explicit offset." };
            return null;
        }

        return parsed;
    }

    private static long? ParseBucketSeconds(string? text, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            errors["bucketSeconds"] = new[] { "bucketSeconds is required." };
            return null;
        }

        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            errors["bucketSeconds"] = new[] { $"bucketSeconds '{text}' must be a positive whole number of seconds." };
            return null;
        }

        if (seconds > MaxBucketSeconds)
        {
            errors["bucketSeconds"] = new[] { "bucketSeconds is too large." };
            return null;
        }

        return seconds;
    }
}
