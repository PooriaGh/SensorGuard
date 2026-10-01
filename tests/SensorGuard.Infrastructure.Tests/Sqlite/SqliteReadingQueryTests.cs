using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;
using Shouldly;
using Xunit;

namespace SensorGuard.Infrastructure.Tests.Sqlite;

public sealed class SqliteReadingQueryTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;

    public async Task InitializeAsync() => _db = await SqliteTestDb.CreateAsync();

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private static DateTimeOffset At(int second) => new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero).AddSeconds(second);

    private static Reading R(string device, int second, long seq, double value, Metric metric = Metric.Temperature) =>
        new(new ReadingKey(new DeviceId(device), metric, At(second), seq), value);

    private async Task SeedAsync(params (Reading Reading, Classification Classification, string[] Rules)[] rows)
    {
        await _db.InTransactionAsync(async ct =>
        {
            foreach (var (reading, _, _) in rows)
            {
                await _db.Readings.InsertIfAbsentAsync(reading, ct);
            }

            await _db.Readings.UpdateClassificationsAsync(
                rows.Select(r => new ReadingClassification(r.Reading.Key, r.Classification, Array.Empty<RuleViolation>())).ToList(), ct);
            foreach (var group in rows.Where(r => r.Rules.Length > 0).GroupBy(r => r.Reading.Series))
            {
                await _db.RuleResults.ReplaceForSeriesAsync(
                    group.Key,
                    group.SelectMany(r => r.Rules.Select(rule => new RuleViolation(r.Reading.Key, new RuleId(rule), "reason " + rule))).ToList(),
                    ct);
            }
        });
    }

    [Fact]
    public async Task Acceptable_returns_only_acceptable_readings_of_the_series_within_the_half_open_range_in_order()
    {
        var series = new SeriesKey(new DeviceId("A"), Metric.Temperature);
        await SeedAsync(
            (R("A", 20, 3, 3), Classification.Acceptable, Array.Empty<string>()),
            (R("A", 10, 2, 2), Classification.Acceptable, Array.Empty<string>()),
            (R("A", 10, 1, 1), Classification.Acceptable, Array.Empty<string>()),
            (R("A", 15, 9, 150), Classification.Unacceptable, new[] { "R1" }),
            (R("A", 60, 4, 4), Classification.Acceptable, Array.Empty<string>()),
            (R("A", 0, 5, 5), Classification.Acceptable, Array.Empty<string>()),
            (R("B", 10, 6, 6), Classification.Acceptable, Array.Empty<string>()),
            (R("A", 10, 7, 7, Metric.Pressure), Classification.Acceptable, Array.Empty<string>()));

        var result = await _db.Query.GetAcceptableAsync(series, At(0), At(60), CancellationToken.None);

        result.Select(r => r.Key.Seq).ShouldBe(new long[] { 5, 1, 2, 3 }); // 60 excluded (to), 15 unacceptable, others other series
        result.Select(r => r.Value).ShouldBe(new double[] { 5, 1, 2, 3 });
    }

    [Fact]
    public async Task Readings_that_have_not_been_classified_are_not_acceptable_yet()
    {
        await _db.InTransactionAsync(async ct => await _db.Readings.InsertIfAbsentAsync(R("A", 0, 1, 1), ct));

        var result = await _db.Query.GetAcceptableAsync(new SeriesKey(new DeviceId("A"), Metric.Temperature), At(0), At(60), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_device_returns_nothing()
    {
        var result = await _db.Query.GetAcceptableAsync(new SeriesKey(new DeviceId("NOPE"), Metric.Temperature), At(0), At(60), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unacceptable_returns_only_unacceptable_readings_with_their_violations()
    {
        await SeedAsync(
            (R("A", 0, 1, 70), Classification.Acceptable, Array.Empty<string>()),
            (R("A", 10, 2, 150), Classification.Unacceptable, new[] { "R1", "R2" }),
            (R("B", 10, 3, 160), Classification.Unacceptable, new[] { "R1" }));

        var all = await _db.Query.GetUnacceptableAsync(null, CancellationToken.None);
        var onlyA = await _db.Query.GetUnacceptableAsync(new SeriesKey(new DeviceId("A"), Metric.Temperature), CancellationToken.None);

        all.Select(r => r.Key.Seq).ShouldBe(new long[] { 2, 3 });
        all[0].Violations.Select(v => v.Rule.Value).ShouldBe(new[] { "R1", "R2" });
        all[0].Violations[0].Reason.ShouldBe("reason R1");
        all[0].Value.ShouldBe(150);
        onlyA.Single().Key.Seq.ShouldBe(2);
    }
}
