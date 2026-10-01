using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;
using SensorGuard.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace SensorGuard.Infrastructure.Tests.Sqlite;

public sealed class SqliteAlertQueryTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;

    public async Task InitializeAsync() => _db = await SqliteTestDb.CreateAsync();

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private static DateTimeOffset At(int second) => new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero).AddSeconds(second);

    [Fact]
    public async Task Alerts_are_returned_with_all_fields_ordered_by_series_then_start()
    {
        var a = new SeriesKey(new DeviceId("A"), Metric.Temperature);
        var b = new SeriesKey(new DeviceId("B"), Metric.Pressure);
        await _db.InTransactionAsync(async ct =>
        {
            await _db.Alerts.ReplaceForSeriesAsync(b, new[] { new Alert(new RuleId("S2"), "Pressure", b, At(5), At(50), 12.5, true) }, ct);
            await _db.Alerts.ReplaceForSeriesAsync(
                a,
                new[]
                {
                    new Alert(new RuleId("S1"), "Heat", a, At(400), At(500), 91, false),
                    new Alert(new RuleId("S1"), "Heat", a, At(10), At(100), 89.5, false),
                },
                ct);
        });

        var alerts = await new SqliteAlertQuery(_db.Database).GetAlertsAsync(CancellationToken.None);

        alerts.Select(x => (x.Series.Device.Value, x.StartTs)).ShouldBe(new[] { ("A", At(10)), ("A", At(400)), ("B", At(5)) });
        alerts[0].RuleName.ShouldBe("Heat");
        alerts[0].PeakValue.ShouldBe(89.5);
        alerts[0].OpenAtEndOfData.ShouldBeFalse();
        alerts[2].OpenAtEndOfData.ShouldBeTrue();
        alerts[2].Series.Metric.ShouldBe(Metric.Pressure);
    }

    [Fact]
    public async Task No_alerts_gives_an_empty_list()
    {
        (await new SqliteAlertQuery(_db.Database).GetAlertsAsync(CancellationToken.None)).ShouldBeEmpty();
    }
}
