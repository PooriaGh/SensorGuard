using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;

namespace SensorGuard.Infrastructure.Sqlite;

public sealed class SqliteAlertQuery : IAlertQuery
{
    private readonly SqliteDatabase _database;

    public SqliteAlertQuery(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<Alert>> GetAlertsAsync(CancellationToken cancellationToken)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT rule_id, rule_name, device_id, metric, start_ts_utc, end_ts_utc, peak_value, open_at_end FROM alerts "
            + "ORDER BY device_id, metric, start_ts_utc, rule_id";

        var alerts = new List<Alert>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            MetricNames.TryParse(reader.GetString(3), out var metric);
            alerts.Add(new Alert(
                new RuleId(reader.GetString(0)),
                reader.GetString(1),
                new SeriesKey(new DeviceId(reader.GetString(2)), metric),
                TimestampFormat.Parse(reader.GetString(4)),
                TimestampFormat.Parse(reader.GetString(5)),
                reader.GetDouble(6),
                reader.GetInt64(7) != 0));
        }

        return alerts;
    }
}
