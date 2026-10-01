using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SensorGuard.Infrastructure.Sqlite;

/// <summary>
/// Fixed-width UTC timestamp text, <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c> (7 fractional digits = .NET tick precision).
/// Lexicographic order equals chronological order, so range queries and the primary-key index work on plain text,
/// and no precision is lost (research R3).
/// </summary>
public static class TimestampFormat
{
    private const string Pattern = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string Format(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) =>
        DateTimeOffset.ParseExact(
            text, Pattern, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}

/// <summary>Opens SQLite connections and creates the schema. The natural keys are the primary keys (Principle VI).</summary>
public sealed class SqliteDatabase
{
    private static readonly string Schema = $$"""
        CREATE TABLE IF NOT EXISTS readings (
            device_id      TEXT    NOT NULL,
            metric         TEXT    NOT NULL,
            ts_utc         TEXT    NOT NULL,
            seq            INTEGER NOT NULL,
            value          REAL    NOT NULL,
            classification TEXT    NULL CHECK (classification IN ('{{ClassificationCodes.Acceptable}}', '{{ClassificationCodes.Unacceptable}}')),
            PRIMARY KEY (device_id, metric, ts_utc, seq)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS rule_results (
            device_id TEXT    NOT NULL,
            metric    TEXT    NOT NULL,
            ts_utc    TEXT    NOT NULL,
            seq       INTEGER NOT NULL,
            rule_id   TEXT    NOT NULL,
            reason    TEXT    NOT NULL,
            PRIMARY KEY (device_id, metric, ts_utc, seq, rule_id),
            FOREIGN KEY (device_id, metric, ts_utc, seq) REFERENCES readings (device_id, metric, ts_utc, seq)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS alerts (
            rule_id      TEXT    NOT NULL,
            device_id    TEXT    NOT NULL,
            metric       TEXT    NOT NULL,
            start_ts_utc TEXT    NOT NULL,
            end_ts_utc   TEXT    NOT NULL,
            peak_value   REAL    NOT NULL,
            open_at_end  INTEGER NOT NULL,
            rule_name    TEXT    NOT NULL,
            PRIMARY KEY (rule_id, device_id, metric, start_ts_utc)
        ) WITHOUT ROWID;
        """;

    private readonly string _connectionString;

    public SqliteDatabase(string connectionString) => _connectionString = connectionString;

    public static SqliteDatabase ForFile(string path) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Holds the transaction of the unit of work that is currently running; stores refuse to run without one.</summary>
public sealed class SqliteSession
{
    public SqliteTransaction? Current { get; internal set; }

    internal SqliteTransaction Require() =>
        Current ?? throw new InvalidOperationException("This store must be used inside a unit of work.");
}
