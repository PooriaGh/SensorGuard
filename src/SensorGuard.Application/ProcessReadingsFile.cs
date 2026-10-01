using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Reporting;
using SensorGuard.Domain.Rules;

namespace SensorGuard.Application;

/// <summary>
/// The ingestion pipeline: read, validate and reject, dedupe, insert-if-absent, load every stored reading of the affected
/// series, evaluate, persist classifications, rule results and alerts, report. Orchestration only: every rule lives in Domain.
/// The whole persistence step is one transaction, and re-evaluation replaces results per series, so re-running the same
/// input changes nothing (Principle VI).
/// </summary>
public sealed class ProcessReadingsFile
{
    private readonly IReadingSource _source;
    private readonly IReadingStore _readings;
    private readonly IRuleResultStore _ruleResults;
    private readonly IAlertStore _alerts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ReadingValidator _validator;
    private readonly Deduplicator _deduplicator;
    private readonly SeriesEvaluator _evaluator;
    private readonly IReportSink _reportSink;
    private readonly TimeProvider _time;
    private readonly ILogger<ProcessReadingsFile> _logger;

    public ProcessReadingsFile(
        IReadingSource source,
        IReadingStore readings,
        IRuleResultStore ruleResults,
        IAlertStore alerts,
        IUnitOfWork unitOfWork,
        ReadingValidator validator,
        Deduplicator deduplicator,
        SeriesEvaluator evaluator,
        IReportSink reportSink,
        TimeProvider time,
        ILogger<ProcessReadingsFile> logger)
    {
        _source = source;
        _readings = readings;
        _ruleResults = ruleResults;
        _alerts = alerts;
        _unitOfWork = unitOfWork;
        _validator = validator;
        _deduplicator = deduplicator;
        _evaluator = evaluator;
        _reportSink = reportSink;
        _time = time;
        _logger = logger;
    }

    public async Task<ProcessingReport> ExecuteAsync(IReadOnlyList<Rule> rules, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        _logger.LogInformation(
            "Loaded {RulesTotal} rules ({RulesEnabled} enabled)", rules.Count, rules.Count(r => r.Enabled));

        var ingestion = await ReadAndValidateAsync(cancellationToken);
        var dedup = _deduplicator.Deduplicate(ingestion.Readings);
        foreach (var conflict in dedup.ConflictingDuplicates)
        {
            _logger.LogWarning(
                "Conflicting duplicate readings for {Key}: kept {KeptValue}, ignored {IgnoredValues}",
                Describe(conflict.Key), conflict.KeptValue, string.Join(", ", conflict.IgnoredValues));
        }

        var outcome = new PersistenceOutcome();
        await _unitOfWork.ExecuteAsync(
            async ct => outcome = await PersistAsync(dedup.Kept, rules, ct),
            cancellationToken);

        var report = BuildReport(rules, ingestion, dedup, outcome, _time.GetElapsedTime(started));
        LogOutcome(outcome.Evaluation);
        _reportSink.Write(ReportFormatter.Format(report));
        LogReport(report);
        foreach (var broken in report.BrokenInvariants())
        {
            _logger.LogWarning("Report invariant does not hold: {Invariant}", broken);
        }

        return report;
    }

    private async Task<Ingestion> ReadAndValidateAsync(CancellationToken cancellationToken)
    {
        var result = new Ingestion();
        await foreach (var line in _source.ReadAsync(cancellationToken))
        {
            result.LinesRead++;
            if (line.IsBlank)
            {
                result.BlankLines++;
            }
            else if (line.ParseFailure is { } failure)
            {
                Reject(result, new Rejection(line.LineNumber, failure, "line is not a JSON object"));
            }
            else
            {
                var validation = _validator.Validate(line.Parsed!);
                if (validation.Rejection is { } rejection)
                {
                    Reject(result, rejection);
                }
                else
                {
                    result.Parsed++;
                    result.Readings.Add(validation.Reading!);
                }
            }
        }

        return result;
    }

    private void Reject(Ingestion result, Rejection rejection)
    {
        result.Invalid++;
        result.InvalidByReason[rejection.Reason] = result.InvalidByReason.GetValueOrDefault(rejection.Reason) + 1;
        _logger.LogWarning(
            "Line {LineNumber} rejected: {Reason} ({Detail})", rejection.LineNumber, rejection.Reason, rejection.Detail);
    }

    private async Task<PersistenceOutcome> PersistAsync(
        IReadOnlyList<Reading> kept, IReadOnlyList<Rule> rules, CancellationToken cancellationToken)
    {
        var outcome = new PersistenceOutcome();
        foreach (var reading in kept)
        {
            switch (await _readings.InsertIfAbsentAsync(reading, cancellationToken))
            {
                case InsertOutcome.Inserted:
                    outcome.NewlyStored++;
                    break;
                case InsertOutcome.AlreadyStoredConflicting:
                    outcome.AlreadyStored++;
                    _logger.LogWarning(
                        "Reading {Key} is already stored with a different value; keeping the stored value (incoming {IncomingValue})",
                        Describe(reading.Key), reading.Value);
                    break;
                default:
                    outcome.AlreadyStored++;
                    break;
            }
        }

        var affected = kept.Select(r => r.Series).Distinct().ToList();
        var stored = await _readings.LoadSeriesAsync(affected, cancellationToken);
        var evaluation = _evaluator.Evaluate(stored, rules);
        outcome.Evaluation = evaluation;
        outcome.FileKeys = kept.Select(r => r.Key).ToHashSet();

        await _readings.UpdateClassificationsAsync(evaluation.Classifications, cancellationToken);
        foreach (var series in affected)
        {
            await _ruleResults.ReplaceForSeriesAsync(
                series, evaluation.Violations.Where(v => v.Reading.Series == series).ToList(), cancellationToken);
            await _alerts.ReplaceForSeriesAsync(
                series, evaluation.Alerts.Where(a => a.Series == series).ToList(), cancellationToken);
        }

        return outcome;
    }

    private ProcessingReport BuildReport(
        IReadOnlyList<Rule> rules, Ingestion ingestion, DedupResult dedup, PersistenceOutcome outcome, TimeSpan elapsed)
    {
        var fileClassifications = outcome.Evaluation.Classifications.Where(c => outcome.FileKeys.Contains(c.Key)).ToList();
        return new ProcessingReport
        {
            LinesRead = ingestion.LinesRead,
            BlankLines = ingestion.BlankLines,
            Parsed = ingestion.Parsed,
            InvalidRejected = ingestion.Invalid,
            InvalidByReason = ingestion.InvalidByReason,
            FileDuplicates = dedup.Duplicates,
            AlreadyStored = outcome.AlreadyStored,
            NewlyStored = outcome.NewlyStored,
            RulesLoadedTotal = rules.Count,
            RulesLoadedEnabled = rules.Count(r => r.Enabled),
            RuleEvaluations = outcome.Evaluation.RuleEvaluations,
            Acceptable = fileClassifications.Count(c => c.Classification == Classification.Acceptable),
            Unacceptable = fileClassifications.Count(c => c.Classification == Classification.Unacceptable),
            RuleViolations = outcome.Evaluation.Violations.Count,
            AlertsGenerated = outcome.Evaluation.Alerts.Count,
            AlertsSuppressed = outcome.Evaluation.Suppressed.Count,
            Elapsed = elapsed,
        };
    }

    private void LogOutcome(SeriesEvaluation evaluation)
    {
        foreach (var detected in evaluation.Episodes)
        {
            _logger.LogInformation(
                "Sustained episode for rule {RuleId} on {Series}: {StartTs} to {EndTs}, peak {Peak}, open at end {OpenAtEnd}",
                detected.Rule, detected.Episode.Series, TimestampText.Format(detected.Episode.StartTs),
                TimestampText.Format(detected.Episode.EndTs), detected.Episode.Peak, detected.Episode.OpenAtEnd);
        }

        foreach (var alert in evaluation.Alerts)
        {
            _logger.LogInformation(
                "Alert raised for rule {RuleId} on {Series} starting {StartTs}", alert.Rule, alert.Series, TimestampText.Format(alert.StartTs));
        }

        foreach (var suppressed in evaluation.Suppressed)
        {
            _logger.LogInformation(
                "Alert suppressed by cooldown for rule {RuleId} on {Series}: episode starting {StartTs} is within the cooldown of the alert starting {AlertStartTs}",
                suppressed.Rule, suppressed.Episode.Series, TimestampText.Format(suppressed.Episode.StartTs), TimestampText.Format(suppressed.SuppressedBy.StartTs));
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var classification in evaluation.Classifications)
            {
                _logger.LogDebug(
                    "Reading {Key} classified {Classification} ({ViolationCount} violations)",
                    Describe(classification.Key), classification.Classification, classification.Violations.Count);
            }
        }
    }

    private static string Describe(ReadingKey key) =>
        $"{key.Series} at {TimestampText.Format(key.Timestamp)} seq {key.Seq}";

    private void LogReport(ProcessingReport report) =>
        _logger.LogInformation(
            "Processing report: {LinesRead} lines read, {BlankLines} blank, {Parsed} parsed, {InvalidRejected} invalid, "
            + "{FileDuplicates} file duplicates, {AlreadyStored} already stored, {NewlyStored} newly stored, "
            + "{RulesLoaded} rules loaded, {RuleEvaluations} rule evaluations, {Acceptable} acceptable, {Unacceptable} unacceptable, "
            + "{RuleViolations} rule violations, {AlertsGenerated} alerts generated, {AlertsSuppressed} alerts suppressed, {ElapsedMs} ms",
            report.LinesRead, report.BlankLines, report.Parsed, report.InvalidRejected, report.FileDuplicates,
            report.AlreadyStored, report.NewlyStored, report.RulesLoadedTotal, report.RuleEvaluations, report.Acceptable,
            report.Unacceptable, report.RuleViolations, report.AlertsGenerated, report.AlertsSuppressed, report.Elapsed.TotalMilliseconds);

    private sealed class Ingestion
    {
        public int LinesRead { get; set; }

        public int BlankLines { get; set; }

        public int Parsed { get; set; }

        public int Invalid { get; set; }

        public Dictionary<RejectionReason, int> InvalidByReason { get; } = new();

        public List<Reading> Readings { get; } = new();
    }

    private sealed class PersistenceOutcome
    {
        public int NewlyStored { get; set; }

        public int AlreadyStored { get; set; }

        public SeriesEvaluation Evaluation { get; set; } = new(
            Array.Empty<ReadingClassification>(), Array.Empty<RuleViolation>(), Array.Empty<DetectedEpisode>(),
            Array.Empty<Alert>(), Array.Empty<SuppressedEpisode>(), 0);

        public HashSet<ReadingKey> FileKeys { get; set; } = new();
    }
}
