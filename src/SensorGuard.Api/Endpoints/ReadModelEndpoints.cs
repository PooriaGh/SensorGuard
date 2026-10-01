using SensorGuard.Application.Ports;
using SensorGuard.Domain.Model;

namespace SensorGuard.Api.Endpoints;

public sealed record AlertDto(
    string RuleId, string RuleName, string DeviceId, string Metric, string StartTs, string EndTs, double PeakValue, bool OpenAtEndOfData);

public sealed record ViolationDto(string RuleId, string Reason);

public sealed record UnacceptableReadingDto(
    string DeviceId, string Metric, string Ts, long Seq, double Value, IReadOnlyList<ViolationDto> Violations);

/// <summary>Optional read-only endpoints that expose alerts and the unacceptable readings as separate outputs (FR-012).</summary>
public static class ReadModelEndpoints
{
    public static IEndpointRouteBuilder MapReadModelEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/alerts", async (IAlertQuery alerts, CancellationToken cancellationToken) =>
            Results.Ok((await alerts.GetAlertsAsync(cancellationToken)).Select(a => new AlertDto(
                a.Rule.Value,
                a.RuleName,
                a.Series.Device.Value,
                MetricNames.Format(a.Series.Metric),
                TimestampText.Format(a.StartTs),
                TimestampText.Format(a.EndTs),
                a.PeakValue,
                a.OpenAtEndOfData)).ToList()));

        api.MapGet("/readings/unacceptable", async (
            string? deviceId, string? metric, IReadingQuery query, CancellationToken cancellationToken) =>
        {
            SeriesKey? series = null;
            if (deviceId is not null || metric is not null)
            {
                var errors = new Dictionary<string, string[]>();
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    errors["deviceId"] = new[] { "deviceId and metric must be given together." };
                }

                if (!MetricNames.TryParse(metric, out var parsed))
                {
                    errors["metric"] = new[] { "metric must be given with deviceId and be one of temperature, pressure, vibration." };
                }

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors, title: "Invalid filter");
                }

                series = new SeriesKey(new DeviceId(deviceId!), parsed);
            }

            var readings = await query.GetUnacceptableAsync(series, cancellationToken);
            return Results.Ok(readings.Select(r => new UnacceptableReadingDto(
                r.Key.Device.Value,
                MetricNames.Format(r.Key.Metric),
                TimestampText.Format(r.Key.Timestamp),
                r.Key.Seq,
                r.Value,
                r.Violations.Select(v => new ViolationDto(v.Rule.Value, v.Reason)).ToList())).ToList());
        });

        return app;
    }
}
