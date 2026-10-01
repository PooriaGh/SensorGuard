using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;

namespace SensorGuard.Application.Tests.Support;

/// <summary>In-memory stand-ins that enforce the same natural keys as the SQL schema (Principle VI).</summary>
internal sealed class InMemoryReadingStore : IReadingStore, IReadingQuery
{
    private readonly Dictionary<ReadingKey, (double Value, Classification? Classification)> _rows = new();

    public Task<InsertOutcome> InsertIfAbsentAsync(Reading reading, CancellationToken cancellationToken)
    {
        if (_rows.TryGetValue(reading.Key, out var existing))
        {
            return Task.FromResult(existing.Value == reading.Value ? InsertOutcome.AlreadyStored : InsertOutcome.AlreadyStoredConflicting);
        }

        _rows[reading.Key] = (reading.Value, null);
        return Task.FromResult(InsertOutcome.Inserted);
    }

    public Task<IReadOnlyList<Reading>> LoadSeriesAsync(IReadOnlyCollection<SeriesKey> series, CancellationToken cancellationToken)
    {
        IReadOnlyList<Reading> result = _rows
            .Where(r => series.Contains(r.Key.Series))
            .Select(r => new Reading(r.Key, r.Value.Value))
            .ToList();
        return Task.FromResult(result);
    }

    public Task UpdateClassificationsAsync(IReadOnlyList<ReadingClassification> classifications, CancellationToken cancellationToken)
    {
        foreach (var c in classifications)
        {
            _rows[c.Key] = (_rows[c.Key].Value, c.Classification);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Reading>> GetAcceptableAsync(SeriesKey series, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        IReadOnlyList<Reading> result = _rows
            .Where(r => r.Key.Series == series && r.Key.Timestamp >= from && r.Key.Timestamp < to && r.Value.Classification == Classification.Acceptable)
            .OrderBy(r => r.Key.Timestamp).ThenBy(r => r.Key.Seq)
            .Select(r => new Reading(r.Key, r.Value.Value))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<UnacceptableReading>> GetUnacceptableAsync(SeriesKey? series, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not needed by the pipeline tests; covered by the SQLite query tests.");

    public int Count => _rows.Count;

    public double? ValueOf(ReadingKey key) => _rows.TryGetValue(key, out var row) ? row.Value : null;

    /// <summary>Order-independent text snapshot for equality assertions.</summary>
    public List<string> Snapshot() => _rows
        .OrderBy(r => r.Key.Device.Value, StringComparer.Ordinal).ThenBy(r => r.Key.Metric).ThenBy(r => r.Key.Timestamp).ThenBy(r => r.Key.Seq)
        .Select(r => $"{r.Key.Device}|{r.Key.Metric}|{r.Key.Timestamp:o}|{r.Key.Seq}|{r.Value.Value}|{r.Value.Classification}")
        .ToList();
}

internal sealed class InMemoryRuleResultStore : IRuleResultStore
{
    private readonly Dictionary<SeriesKey, List<RuleViolation>> _bySeries = new();

    public Task ReplaceForSeriesAsync(SeriesKey series, IReadOnlyList<RuleViolation> violations, CancellationToken cancellationToken)
    {
        var duplicate = violations.GroupBy(v => (v.Reading, v.Rule)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Unique key violated for rule result {duplicate.Key}.");
        }

        _bySeries[series] = violations.ToList();
        return Task.CompletedTask;
    }

    public int Count => _bySeries.Values.Sum(v => v.Count);

    public List<string> Snapshot() => _bySeries.Values.SelectMany(v => v)
        .Select(v => $"{v.Reading.Device}|{v.Reading.Metric}|{v.Reading.Timestamp:o}|{v.Reading.Seq}|{v.Rule}|{v.Reason}")
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();
}

internal sealed class InMemoryAlertStore : IAlertStore
{
    private readonly Dictionary<SeriesKey, List<Alert>> _bySeries = new();

    public Task ReplaceForSeriesAsync(SeriesKey series, IReadOnlyList<Alert> alerts, CancellationToken cancellationToken)
    {
        var duplicate = alerts.GroupBy(a => (a.Rule, a.Series, a.StartTs)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Unique key violated for alert {duplicate.Key}.");
        }

        _bySeries[series] = alerts.ToList();
        return Task.CompletedTask;
    }

    public List<Alert> All => _bySeries.Values.SelectMany(a => a).OrderBy(a => a.Series.Device.Value, StringComparer.Ordinal)
        .ThenBy(a => a.Series.Metric).ThenBy(a => a.StartTs).ToList();

    public List<string> Snapshot() => All
        .Select(a => $"{a.Rule}|{a.Series}|{a.StartTs:o}|{a.EndTs:o}|{a.PeakValue}|{a.OpenAtEndOfData}")
        .ToList();
}

internal sealed class PassThroughUnitOfWork : IUnitOfWork
{
    public int Executions { get; private set; }

    public async Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        Executions++;
        await work(cancellationToken);
    }
}
