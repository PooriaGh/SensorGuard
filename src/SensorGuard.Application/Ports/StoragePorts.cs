using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;

namespace SensorGuard.Application.Ports;

public enum InsertOutcome
{
    Inserted,
    AlreadyStored,

    /// <summary>The key exists with a different value; the stored value is kept (research R16).</summary>
    AlreadyStoredConflicting,
}

public interface IReadingStore
{
    /// <summary>Insert-if-absent keyed by (device, metric, ts, seq); never overwrites an existing reading.</summary>
    Task<InsertOutcome> InsertIfAbsentAsync(Reading reading, CancellationToken cancellationToken);

    /// <summary>Every stored reading of the given series.</summary>
    Task<IReadOnlyList<Reading>> LoadSeriesAsync(IReadOnlyCollection<SeriesKey> series, CancellationToken cancellationToken);

    Task UpdateClassificationsAsync(IReadOnlyList<ReadingClassification> classifications, CancellationToken cancellationToken);
}

public interface IRuleResultStore
{
    /// <summary>Replaces all stored violations of a series, so stale results never linger (idempotent re-evaluation).</summary>
    Task ReplaceForSeriesAsync(SeriesKey series, IReadOnlyList<RuleViolation> violations, CancellationToken cancellationToken);
}

public interface IAlertStore
{
    /// <summary>Replaces all stored alerts of a series, so stale alerts never linger (idempotent re-evaluation).</summary>
    Task ReplaceForSeriesAsync(SeriesKey series, IReadOnlyList<Alert> alerts, CancellationToken cancellationToken);
}

/// <summary>Runs the given work as one all-or-nothing transaction.</summary>
public interface IUnitOfWork
{
    Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}

public sealed record UnacceptableReading(ReadingKey Key, double Value, IReadOnlyList<RuleViolation> Violations);

/// <summary>Read side: acceptable and unacceptable readings are separate outputs (FR-012).</summary>
public interface IReadingQuery
{
    /// <summary>Acceptable readings of a series with from &lt;= ts &lt; to, ordered by (ts, seq).</summary>
    Task<IReadOnlyList<Reading>> GetAcceptableAsync(SeriesKey series, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    Task<IReadOnlyList<UnacceptableReading>> GetUnacceptableAsync(SeriesKey? series, CancellationToken cancellationToken);
}

/// <summary>Read side for alerts.</summary>
public interface IAlertQuery
{
    Task<IReadOnlyList<Alert>> GetAlertsAsync(CancellationToken cancellationToken);
}

/// <summary>Where the end-of-run report text is printed (console in production).</summary>
public interface IReportSink
{
    void Write(string text);
}
