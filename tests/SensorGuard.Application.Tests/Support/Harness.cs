using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Domain.Reporting;
using SensorGuard.Domain.Rules;
using SensorGuard.Domain.Rules.Operators;

namespace SensorGuard.Application.Tests.Support;

internal sealed record LogEntry(LogLevel Level, string Message);

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = new();

    /// <summary>Mimics production filtering: Debug is dropped unless a test lowers the level.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}

internal sealed class CollectingReportSink : IReportSink
{
    public List<string> Written { get; } = new();

    public void Write(string text) => Written.Add(text);
}

internal sealed class FakeReadingSource : IReadingSource
{
    private readonly IReadOnlyList<RawLine> _lines;

    public FakeReadingSource(IEnumerable<RawLine> lines)
    {
        // Line numbers are assigned by position so tests can shuffle freely.
        _lines = lines.Select((l, i) => Renumber(l, i + 1)).ToList();
    }

    public async IAsyncEnumerable<RawLine> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var line in _lines)
        {
            yield return line;
        }

        await Task.CompletedTask;
    }

    private static RawLine Renumber(RawLine line, int number) =>
        line with { LineNumber = number, Parsed = line.Parsed is null ? null : line.Parsed with { LineNumber = number } };
}

/// <summary>Builders for input lines.</summary>
internal static class Lines
{
    public static RawLine Reading(string device, string metric, string ts, double value, long seq) =>
        RawLine.Ok(new RawReading(0, Field.Str(device), Field.Str(metric), Field.Str(ts), Field.Num(value), Field.Int(seq)));

    public static RawLine Temp(string device, string ts, double value, long seq) => Reading(device, "temperature", ts, value, seq);

    public static RawLine Blank() => RawLine.Blank(0);

    public static RawLine Malformed() => RawLine.Failed(0, RejectionReason.MalformedJson);

    public static RawLine WithRaw(Field deviceId, Field metric, Field ts, Field value, Field seq) =>
        RawLine.Ok(new RawReading(0, deviceId, metric, ts, value, seq));

    public static List<RawLine> Shuffled(IEnumerable<RawLine> lines, int seed)
    {
        var list = lines.ToList();
        var rng = new Random(seed);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    /// <summary>08:00:00Z plus the given seconds, ISO text.</summary>
    public static string At(int seconds) =>
        new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero).AddSeconds(seconds)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Wires the real domain services to in-memory stores.</summary>
internal sealed class Harness
{
    public InMemoryReadingStore Readings { get; } = new();

    public InMemoryRuleResultStore RuleResults { get; } = new();

    public InMemoryAlertStore Alerts { get; } = new();

    public PassThroughUnitOfWork UnitOfWork { get; } = new();

    public CapturingLogger<ProcessReadingsFile> Logger { get; } = new();

    public CollectingReportSink Sink { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    public IReadOnlyList<Rule> Rules { get; set; } = Array.Empty<Rule>();

    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<Rule> BuildRules(params RuleDefinition[] definitions)
    {
        var registry = new OperatorRegistry(
            new IStatelessOperator[]
            {
                new GreaterThanOperator(), new GreaterThanOrEqualOperator(), new LessThanOperator(),
                new LessThanOrEqualOperator(), new EqualOperator(), new BetweenOperator(),
            },
            new IStatefulOperator[] { new SustainedAboveOperator() });
        return new RuleCatalog(registry).Build(definitions);
    }

    public static RuleDefinition GreaterThan(string id, string metric, double value, bool enabled = true) =>
        new(id, id + " name", enabled, metric, null, "GreaterThan", new Dictionary<string, double> { ["value"] = value });

    public static RuleDefinition Sustained(string id, string metric, double threshold, double seconds) =>
        new(id, id + " name", true, metric, null, "SustainedAbove",
            new Dictionary<string, double> { ["threshold"] = threshold, ["durationSeconds"] = seconds });

    public Task<ProcessingReport> RunAsync(IEnumerable<RawLine> lines)
    {
        var registry = new OperatorRegistry(
            new IStatelessOperator[]
            {
                new GreaterThanOperator(), new GreaterThanOrEqualOperator(), new LessThanOperator(),
                new LessThanOrEqualOperator(), new EqualOperator(), new BetweenOperator(),
            },
            new IStatefulOperator[] { new SustainedAboveOperator() });
        var useCase = new ProcessReadingsFile(
            new FakeReadingSource(lines),
            Readings,
            RuleResults,
            Alerts,
            UnitOfWork,
            new ReadingValidator(),
            new Deduplicator(),
            new SeriesEvaluator(registry, new AlertPolicy(Cooldown)),
            Sink,
            Time,
            Logger);
        return useCase.ExecuteAsync(Rules, CancellationToken.None);
    }
}
