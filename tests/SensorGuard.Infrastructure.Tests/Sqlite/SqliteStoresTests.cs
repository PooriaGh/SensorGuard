using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;
using SensorGuard.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace SensorGuard.Infrastructure.Tests.Sqlite;

public sealed class SqliteStoresTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private readonly CancellationToken _ct = CancellationToken.None;

    public async Task InitializeAsync() => _db = await SqliteTestDb.CreateAsync();

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();

    private static Reading R(string device, string ts, long seq, double value, Metric metric = Metric.Temperature) =>
        new(new ReadingKey(new DeviceId(device), metric, At(ts), seq), value);

    [Fact]
    public async Task Insert_if_absent_reports_inserted_then_already_stored_and_never_overwrites()
    {
        var reading = R("A", "2025-06-01T08:00:00Z", 1, 70);
        InsertOutcome first = default, same = default, conflicting = default;

        await _db.InTransactionAsync(async ct =>
        {
            first = await _db.Readings.InsertIfAbsentAsync(reading, ct);
            same = await _db.Readings.InsertIfAbsentAsync(reading, ct);
            conflicting = await _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:00Z", 1, 99), ct);
        });

        first.ShouldBe(InsertOutcome.Inserted);
        same.ShouldBe(InsertOutcome.AlreadyStored);
        conflicting.ShouldBe(InsertOutcome.AlreadyStoredConflicting);
        _db.Scalar("select count(*) from readings").ShouldBe(1);
        _db.Scalar("select cast(value as integer) from readings").ShouldBe(70);
    }

    [Fact]
    public async Task Same_timestamp_with_a_different_seq_or_device_or_metric_are_separate_rows()
    {
        await _db.InTransactionAsync(async ct =>
        {
            await _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:00Z", 1, 70), ct);
            await _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:00Z", 2, 70), ct);
            await _db.Readings.InsertIfAbsentAsync(R("B", "2025-06-01T08:00:00Z", 1, 70), ct);
            await _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:00Z", 1, 70, Metric.Pressure), ct);
        });

        _db.Scalar("select count(*) from readings").ShouldBe(4);
    }

    [Fact]
    public async Task Load_series_returns_only_the_requested_series_in_event_time_then_seq_order()
    {
        await _db.InTransactionAsync(async ct =>
        {
            await _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:20Z", 3, 3), ct);
            await _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:10Z", 2, 2), ct);
            await _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:10Z", 1, 1), ct);
            await _db.Readings.InsertIfAbsentAsync(R("B", "2025-06-01T08:00:00Z", 9, 9), ct);
            await _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:00Z", 8, 8, Metric.Pressure), ct);
        });
        IReadOnlyList<Reading> loaded = Array.Empty<Reading>();

        await _db.InTransactionAsync(async ct =>
            loaded = await _db.Readings.LoadSeriesAsync(new[] { new SeriesKey(new DeviceId("A"), Metric.Temperature) }, ct));

        loaded.Select(r => r.Key.Seq).ShouldBe(new long[] { 1, 2, 3 });
        loaded.Select(r => r.Value).ShouldBe(new double[] { 1, 2, 3 });
    }

    [Fact]
    public async Task Timestamps_round_trip_with_tick_precision_and_sort_chronologically_as_text()
    {
        var precise = new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
        var earlier = new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2025, 6, 1, 8, 0, 1, TimeSpan.Zero);

        TimestampFormat.Parse(TimestampFormat.Format(precise)).ShouldBe(precise);
        TimestampFormat.Format(earlier).ShouldBe("2025-06-01T08:00:00.0000000Z");
        string.CompareOrdinal(TimestampFormat.Format(earlier), TimestampFormat.Format(precise)).ShouldBeLessThan(0);
        string.CompareOrdinal(TimestampFormat.Format(precise), TimestampFormat.Format(later)).ShouldBeLessThan(0);

        await _db.InTransactionAsync(async ct =>
        {
            await _db.Readings.InsertIfAbsentAsync(new Reading(new ReadingKey(new DeviceId("A"), Metric.Temperature, precise, 1), 1), ct);
            await _db.Readings.InsertIfAbsentAsync(new Reading(new ReadingKey(new DeviceId("A"), Metric.Temperature, precise.AddTicks(1), 1), 1), ct);
        });
        _db.Scalar("select count(*) from readings").ShouldBe(2); // 100 ns apart: not merged
    }

    [Fact]
    public async Task Classifications_are_updated_in_place()
    {
        var reading = R("A", "2025-06-01T08:00:00Z", 1, 70);
        await _db.InTransactionAsync(async ct =>
        {
            await _db.Readings.InsertIfAbsentAsync(reading, ct);
            await _db.Readings.UpdateClassificationsAsync(
                new[] { new ReadingClassification(reading.Key, Classification.Unacceptable, Array.Empty<RuleViolation>()) }, ct);
        });

        _db.Scalar("select count(*) from readings where classification = 'U'").ShouldBe(1);
    }

    [Fact]
    public async Task Replacing_rule_results_for_a_series_removes_stale_rows_and_keeps_other_series()
    {
        var a = R("A", "2025-06-01T08:00:00Z", 1, 70);
        var b = R("B", "2025-06-01T08:00:00Z", 1, 70);
        var seriesA = a.Series;
        await _db.InTransactionAsync(async ct =>
        {
            await _db.Readings.InsertIfAbsentAsync(a, ct);
            await _db.Readings.InsertIfAbsentAsync(b, ct);
            await _db.RuleResults.ReplaceForSeriesAsync(seriesA, new[] { new RuleViolation(a.Key, new RuleId("R1"), "old") }, ct);
            await _db.RuleResults.ReplaceForSeriesAsync(b.Series, new[] { new RuleViolation(b.Key, new RuleId("R1"), "other series") }, ct);
        });

        await _db.InTransactionAsync(ct => _db.RuleResults.ReplaceForSeriesAsync(
            seriesA, new[] { new RuleViolation(a.Key, new RuleId("R2"), "new") }, ct));

        _db.Scalar("select count(*) from rule_results").ShouldBe(2);
        _db.Scalar("select count(*) from rule_results where rule_id = 'R1' and device_id = 'A'").ShouldBe(0);
        _db.Scalar("select count(*) from rule_results where rule_id = 'R2' and device_id = 'A'").ShouldBe(1);
        _db.Scalar("select count(*) from rule_results where device_id = 'B'").ShouldBe(1);
    }

    [Fact]
    public async Task Writing_the_same_rule_results_twice_leaves_one_row_per_key()
    {
        var a = R("A", "2025-06-01T08:00:00Z", 1, 70);
        var violations = new[] { new RuleViolation(a.Key, new RuleId("R1"), "x") };
        await _db.InTransactionAsync(async ct => await _db.Readings.InsertIfAbsentAsync(a, ct));

        await _db.InTransactionAsync(ct => _db.RuleResults.ReplaceForSeriesAsync(a.Series, violations, ct));
        await _db.InTransactionAsync(ct => _db.RuleResults.ReplaceForSeriesAsync(a.Series, violations, ct));

        _db.Scalar("select count(*) from rule_results").ShouldBe(1);
    }

    [Fact]
    public async Task A_duplicate_rule_result_key_in_one_batch_is_rejected_by_the_database()
    {
        var a = R("A", "2025-06-01T08:00:00Z", 1, 70);
        var duplicate = new[]
        {
            new RuleViolation(a.Key, new RuleId("R1"), "x"),
            new RuleViolation(a.Key, new RuleId("R1"), "y"),
        };
        await _db.InTransactionAsync(async ct => await _db.Readings.InsertIfAbsentAsync(a, ct));

        await Should.ThrowAsync<SqliteException>(
            () => _db.InTransactionAsync(ct => _db.RuleResults.ReplaceForSeriesAsync(a.Series, duplicate, ct)));
    }

    [Fact]
    public async Task A_rule_result_for_a_reading_that_does_not_exist_is_rejected()
    {
        var ghost = R("A", "2025-06-01T08:00:00Z", 1, 70);

        await Should.ThrowAsync<SqliteException>(() => _db.InTransactionAsync(ct =>
            _db.RuleResults.ReplaceForSeriesAsync(ghost.Series, new[] { new RuleViolation(ghost.Key, new RuleId("R1"), "x") }, ct)));
    }

    [Fact]
    public async Task Alerts_are_replaced_per_series_and_keyed_by_rule_series_and_start()
    {
        var series = new SeriesKey(new DeviceId("A"), Metric.Temperature);
        var alert = new Alert(new RuleId("S1"), "Sustained", series, At("2025-06-01T08:00:20Z"), At("2025-06-01T08:01:10Z"), 89, false);

        await _db.InTransactionAsync(ct => _db.Alerts.ReplaceForSeriesAsync(series, new[] { alert }, ct));
        await _db.InTransactionAsync(ct => _db.Alerts.ReplaceForSeriesAsync(series, new[] { alert }, ct));
        _db.Scalar("select count(*) from alerts").ShouldBe(1);

        await _db.InTransactionAsync(ct => _db.Alerts.ReplaceForSeriesAsync(
            series, new[] { alert with { StartTs = At("2025-06-01T08:00:10Z") } }, ct));
        _db.Scalar("select count(*) from alerts").ShouldBe(1);
        _db.Scalar("select count(*) from alerts where start_ts_utc = '2025-06-01T08:00:10.0000000Z'").ShouldBe(1);

        await Should.ThrowAsync<SqliteException>(
            () => _db.InTransactionAsync(ct => _db.Alerts.ReplaceForSeriesAsync(series, new[] { alert, alert }, ct)));
    }

    [Fact]
    public async Task The_unit_of_work_rolls_everything_back_when_the_work_throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _db.InTransactionAsync(async ct =>
        {
            await _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:00Z", 1, 70), ct);
            throw new InvalidOperationException("boom");
        }));

        _db.Scalar("select count(*) from readings").ShouldBe(0);
    }

    [Fact]
    public async Task The_unit_of_work_commits_when_the_work_succeeds()
    {
        await _db.InTransactionAsync(async ct => await _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:00Z", 1, 70), ct));

        _db.Scalar("select count(*) from readings").ShouldBe(1);
    }

    [Fact]
    public async Task Stores_refuse_to_run_outside_a_unit_of_work()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => _db.Readings.InsertIfAbsentAsync(R("A", "2025-06-01T08:00:00Z", 1, 70), _ct));
    }

    [Fact]
    public async Task Initializing_the_schema_twice_is_harmless()
    {
        await _db.Database.InitializeAsync(_ct);

        _db.Scalar("select count(*) from sqlite_master where name in ('readings','rule_results','alerts')").ShouldBe(3);
    }
}
