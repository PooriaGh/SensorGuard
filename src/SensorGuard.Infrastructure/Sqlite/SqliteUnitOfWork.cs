using System;
using System.Threading;
using System.Threading.Tasks;
using SensorGuard.Application.Ports;

namespace SensorGuard.Infrastructure.Sqlite;

/// <summary>One connection and one transaction for the whole run: commit when the work succeeds, roll back otherwise.</summary>
public sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteDatabase _database;
    private readonly SqliteSession _session;

    public SqliteUnitOfWork(SqliteDatabase database, SqliteSession session)
    {
        _database = database;
        _session = session;
    }

    public async Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        if (_session.Current is not null)
        {
            throw new InvalidOperationException("A unit of work is already running.");
        }

        await using var connection = _database.OpenConnection();
        await using var transaction = connection.BeginTransaction();
        _session.Current = transaction;
        try
        {
            await work(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            _session.Current = null;
        }
    }
}
