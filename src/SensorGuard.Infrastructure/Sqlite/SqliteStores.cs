using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;

namespace SensorGuard.Infrastructure.Sqlite;

internal static class SqliteMapping
{
    public static SqliteCommand Command(SqliteTransaction transaction, string sql)
    {
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    public static Reading ReadReading(SqliteDataReader reader, int offset = 0)
    {
        MetricNames.TryParse(reader.GetString(offset + 1), out var metric);
        var key = new ReadingKey(
            new DeviceId(reader.GetString(offset)),
            metric,
            TimestampFormat.Parse(reader.GetString(offset + 2)),
            reader.GetInt64(offset + 3));
        return new Reading(key, reader.GetDouble(offset + 4));
    }
}

public sealed class SqliteReadingStore : IReadingStore
{
    private readonly SqliteSession _session;

    public SqliteReadingStore(SqliteSession session) => _session = session;

    public async Task<InsertOutcome> InsertIfAbsentAsync(Reading reading, CancellationToken cancellationToken)
    {
        var transaction = _session.Require();
        await using (var insert = SqliteMapping.Command(
            transaction,
            "INSERT INTO readings (device_id, metric, ts_utc, seq, value) VALUES ($d, $m, $t, $s, $v) ON CONFLICT DO NOTHING"))
        {
            BindKey(insert, reading.Key);
            insert.Parameters.AddWithValue("$v", reading.Value);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return InsertOutcome.Inserted;
            }
        }

        await using var select = SqliteMapping.Command(
            transaction,
            "SELECT value FROM readings WHERE device_id = $d AND metric = $m AND ts_utc = $t AND seq = $s");
        BindKey(select, reading.Key);
        var stored = Convert.ToDouble(await select.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return stored == reading.Value ? InsertOutcome.AlreadyStored : InsertOutcome.AlreadyStoredConflicting;
    }

    public async Task<IReadOnlyList<Reading>> LoadSeriesAsync(
        IReadOnlyCollection<SeriesKey> series, CancellationToken cancellationToken)
    {
        var transaction = _session.Require();
        var result = new List<Reading>();
        foreach (var key in series)
        {
            await using var command = SqliteMapping.Command(
                transaction,
                "SELECT device_id, metric, ts_utc, seq, value FROM readings WHERE device_id = $d AND metric = $m ORDER BY ts_utc, seq");
            command.Parameters.AddWithValue("$d", key.Device.Value);
            command.Parameters.AddWithValue("$m", MetricNames.Format(key.Metric));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(SqliteMapping.ReadReading(reader));
            }
        }

        return result;
    }

    public async Task UpdateClassificationsAsync(
        IReadOnlyList<ReadingClassification> classifications, CancellationToken cancellationToken)
    {
        var transaction = _session.Require();
        await using var command = SqliteMapping.Command(
            transaction,
            "UPDATE readings SET classification = $c WHERE device_id = $d AND metric = $m AND ts_utc = $t AND seq = $s");
        var classification = command.Parameters.Add("$c", SqliteType.Text);
        var device = command.Parameters.Add("$d", SqliteType.Text);
        var metric = command.Parameters.Add("$m", SqliteType.Text);
        var timestamp = command.Parameters.Add("$t", SqliteType.Text);
        var seq = command.Parameters.Add("$s", SqliteType.Integer);
        foreach (var item in classifications)
        {
            classification.Value = ClassificationCodes.For(item.Classification);
            device.Value = item.Key.Device.Value;
            metric.Value = MetricNames.Format(item.Key.Metric);
            timestamp.Value = TimestampFormat.Format(item.Key.Timestamp);
            seq.Value = item.Key.Seq;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void BindKey(SqliteCommand command, ReadingKey key)
    {
        command.Parameters.AddWithValue("$d", key.Device.Value);
        command.Parameters.AddWithValue("$m", MetricNames.Format(key.Metric));
        command.Parameters.AddWithValue("$t", TimestampFormat.Format(key.Timestamp));
        command.Parameters.AddWithValue("$s", key.Seq);
    }
}

public sealed class SqliteRuleResultStore : IRuleResultStore
{
    private readonly SqliteSession _session;

    public SqliteRuleResultStore(SqliteSession session) => _session = session;

    public async Task ReplaceForSeriesAsync(
        SeriesKey series, IReadOnlyList<RuleViolation> violations, CancellationToken cancellationToken)
    {
        var transaction = _session.Require();
        await using (var delete = SqliteMapping.Command(
            transaction, "DELETE FROM rule_results WHERE device_id = $d AND metric = $m"))
        {
            delete.Parameters.AddWithValue("$d", series.Device.Value);
            delete.Parameters.AddWithValue("$m", MetricNames.Format(series.Metric));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = SqliteMapping.Command(
            transaction,
            "INSERT INTO rule_results (device_id, metric, ts_utc, seq, rule_id, reason) VALUES ($d, $m, $t, $s, $r, $why)");
        var device = insert.Parameters.Add("$d", SqliteType.Text);
        var metric = insert.Parameters.Add("$m", SqliteType.Text);
        var timestamp = insert.Parameters.Add("$t", SqliteType.Text);
        var seq = insert.Parameters.Add("$s", SqliteType.Integer);
        var rule = insert.Parameters.Add("$r", SqliteType.Text);
        var reason = insert.Parameters.Add("$why", SqliteType.Text);
        foreach (var violation in violations)
        {
            device.Value = violation.Reading.Device.Value;
            metric.Value = MetricNames.Format(violation.Reading.Metric);
            timestamp.Value = TimestampFormat.Format(violation.Reading.Timestamp);
            seq.Value = violation.Reading.Seq;
            rule.Value = violation.Rule.Value;
            reason.Value = violation.Reason;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

public sealed class SqliteAlertStore : IAlertStore
{
    private readonly SqliteSession _session;

    public SqliteAlertStore(SqliteSession session) => _session = session;

    public async Task ReplaceForSeriesAsync(
        SeriesKey series, IReadOnlyList<Alert> alerts, CancellationToken cancellationToken)
    {
        var transaction = _session.Require();
        await using (var delete = SqliteMapping.Command(
            transaction, "DELETE FROM alerts WHERE device_id = $d AND metric = $m"))
        {
            delete.Parameters.AddWithValue("$d", series.Device.Value);
            delete.Parameters.AddWithValue("$m", MetricNames.Format(series.Metric));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = SqliteMapping.Command(
            transaction,
            "INSERT INTO alerts (rule_id, device_id, metric, start_ts_utc, end_ts_utc, peak_value, open_at_end, rule_name) "
            + "VALUES ($r, $d, $m, $start, $end, $peak, $open, $name)");
        foreach (var alert in alerts)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$r", alert.Rule.Value);
            insert.Parameters.AddWithValue("$d", alert.Series.Device.Value);
            insert.Parameters.AddWithValue("$m", MetricNames.Format(alert.Series.Metric));
            insert.Parameters.AddWithValue("$start", TimestampFormat.Format(alert.StartTs));
            insert.Parameters.AddWithValue("$end", TimestampFormat.Format(alert.EndTs));
            insert.Parameters.AddWithValue("$peak", alert.PeakValue);
            insert.Parameters.AddWithValue("$open", alert.OpenAtEndOfData ? 1 : 0);
            insert.Parameters.AddWithValue("$name", alert.RuleName);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

public sealed class SqliteReadingQuery : IReadingQuery
{
    private readonly SqliteDatabase _database;

    public SqliteReadingQuery(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<Reading>> GetAcceptableAsync(
        SeriesKey series, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT device_id, metric, ts_utc, seq, value FROM readings "
            + "WHERE device_id = $d AND metric = $m AND classification = '" + ClassificationCodes.Acceptable + "' AND ts_utc >= $from AND ts_utc < $to "
            + "ORDER BY ts_utc, seq";
        command.Parameters.AddWithValue("$d", series.Device.Value);
        command.Parameters.AddWithValue("$m", MetricNames.Format(series.Metric));
        command.Parameters.AddWithValue("$from", TimestampFormat.Format(from));
        command.Parameters.AddWithValue("$to", TimestampFormat.Format(to));

        var result = new List<Reading>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(SqliteMapping.ReadReading(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<UnacceptableReading>> GetUnacceptableAsync(
        SeriesKey? series, CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT r.device_id, r.metric, r.ts_utc, r.seq, r.value, v.rule_id, v.reason FROM readings r "
            + "LEFT JOIN rule_results v ON v.device_id = r.device_id AND v.metric = r.metric AND v.ts_utc = r.ts_utc AND v.seq = r.seq "
            + "WHERE r.classification = '" + ClassificationCodes.Unacceptable + "' "
            + (series is null ? string.Empty : "AND r.device_id = $d AND r.metric = $m ")
            + "ORDER BY r.device_id, r.metric, r.ts_utc, r.seq, v.rule_id";
        if (series is { } s)
        {
            command.Parameters.AddWithValue("$d", s.Device.Value);
            command.Parameters.AddWithValue("$m", MetricNames.Format(s.Metric));
        }

        var result = new List<UnacceptableReading>();
        Reading? current = null;
        var violations = new List<RuleViolation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var reading = SqliteMapping.ReadReading(reader);
            if (current is null || current.Key != reading.Key)
            {
                Flush();
                current = reading;
            }

            if (!reader.IsDBNull(5))
            {
                violations.Add(new RuleViolation(reading.Key, new RuleId(reader.GetString(5)), reader.GetString(6)));
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (current is not null)
            {
                result.Add(new UnacceptableReading(current.Key, current.Value, new List<RuleViolation>(violations)));
                violations.Clear();
            }
        }
    }
}
