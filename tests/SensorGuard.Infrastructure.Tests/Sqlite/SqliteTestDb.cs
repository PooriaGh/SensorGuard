using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SensorGuard.Infrastructure.Sqlite;

namespace SensorGuard.Infrastructure.Tests.Sqlite;

/// <summary>A private shared-cache in-memory SQLite database that lives as long as this object (a keeper connection stays open).</summary>
internal sealed class SqliteTestDb : IDisposable
{
    private readonly SqliteConnection _keeper;

    private SqliteTestDb(SqliteDatabase database)
    {
        Database = database;
        Session = new SqliteSession();
        UnitOfWork = new SqliteUnitOfWork(database, Session);
        Readings = new SqliteReadingStore(Session);
        RuleResults = new SqliteRuleResultStore(Session);
        Alerts = new SqliteAlertStore(Session);
        Query = new SqliteReadingQuery(database);
        _keeper = database.OpenConnection();
    }

    public SqliteDatabase Database { get; }

    public SqliteSession Session { get; }

    public SqliteUnitOfWork UnitOfWork { get; }

    public SqliteReadingStore Readings { get; }

    public SqliteRuleResultStore RuleResults { get; }

    public SqliteAlertStore Alerts { get; }

    public SqliteReadingQuery Query { get; }

    public static async Task<SqliteTestDb> CreateAsync()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = "file:sg-" + Guid.NewGuid().ToString("N"),
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        var db = new SqliteTestDb(new SqliteDatabase(connectionString));
        await db.Database.InitializeAsync(CancellationToken.None);
        return db;
    }

    public long Scalar(string sql)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task InTransactionAsync(Func<CancellationToken, Task> work) => UnitOfWork.ExecuteAsync(work, CancellationToken.None);

    public void Dispose() => _keeper.Dispose();
}
