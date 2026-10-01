using SensorGuard.Application;
using SensorGuard.Domain.Model;

namespace SensorGuard.Api.Endpoints;

public sealed record BucketDto(string Start, int Count, double? Average, double? Min, double? Max);

public sealed record AggregationResponse(
    string DeviceId, string Metric, string From, string To, long BucketSeconds, IReadOnlyList<BucketDto> Buckets);

public static class AggregationEndpoints
{
    /// <summary>Maps the request, calls the use case, maps the result: no logic of its own.</summary>
    public static IEndpointRouteBuilder MapAggregationEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/aggregations", async (
            string? deviceId, string? metric, string? from, string? to, string? bucketSeconds,
            GetAggregation useCase, CancellationToken cancellationToken) =>
        {
            var result = await useCase.ExecuteAsync(new AggregationQuery(deviceId, metric, from, to, bucketSeconds), cancellationToken);
            if (!result.IsValid)
            {
                return Results.ValidationProblem(result.Errors, title: "Invalid aggregation request");
            }

            var query = result.Query!;
            return Results.Ok(new AggregationResponse(
                query.Series.Device.Value,
                MetricNames.Format(query.Series.Metric),
                TimestampText.Format(query.From),
                TimestampText.Format(query.To),
                query.BucketSeconds,
                result.Buckets
                    .Select(b => new BucketDto(TimestampText.Format(b.Start), b.Count, b.Average, b.Min, b.Max))
                    .ToList()));
        });

        return app;
    }
}
