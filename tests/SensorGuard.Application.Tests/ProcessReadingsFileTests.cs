using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SensorGuard.Application.Ports;
using SensorGuard.Application.Tests.Support;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Domain.Model;
using Shouldly;
using Xunit;

namespace SensorGuard.Application.Tests;

public sealed class ProcessReadingsFileTests
{
    private static Harness WithRules(params SensorGuard.Domain.Rules.RuleDefinition[] definitions) =>
        new() { Rules = Harness.BuildRules(definitions) };

    // 10 s steps from 08:00:00: above 80 during seconds 20..60 (ending reading at 70) => one sustained episode.
    private static List<RawLine> EpisodeFile(string device = "PUMP-01") =>
        new[] { 70.0, 71, 85, 86, 87, 88, 89, 70, 70 }
            .Select((v, i) => Lines.Temp(device, Lines.At(i * 10), v, i + 1))
            .ToList();

    [Fact]
    public async Task A_mixed_file_is_counted_exactly_and_the_report_reconciles()
    {
        var h = WithRules(Harness.GreaterThan("R1", "temperature", 100));
        var lines = new List<RawLine>
        {
            Lines.Temp("A", Lines.At(0), 50, 1),                  // valid
            Lines.Temp("A", Lines.At(10), 150, 2),                // valid, violates R1
            Lines.Temp("A", Lines.At(0), 50, 1),                  // identical duplicate
            Lines.Temp("B", Lines.At(0), 60, 3),                  // valid
            Lines.Temp("B", Lines.At(0), 55, 3),                  // conflicting duplicate (lowest 55 wins)
            Lines.Blank(),
            Lines.Malformed(),
            Lines.Reading("A", "humidity", Lines.At(20), 1, 4),  // UnknownMetric
            Lines.WithRaw(Field.Missing, Field.Str("temperature"), Field.Str(Lines.At(0)), Field.Num(1), Field.Int(5)), // MissingField
            Lines.WithRaw(Field.Str("A"), Field.Str("temperature"), Field.Str("2025-06-01T08:00:05"), Field.Num(1), Field.Int(6)), // InvalidTimestamp
        };

        var report = await h.RunAsync(lines);

        report.LinesRead.ShouldBe(10);
        report.BlankLines.ShouldBe(1);
        report.Parsed.ShouldBe(5);
        report.InvalidRejected.ShouldBe(4);
        report.InvalidByReason[RejectionReason.MalformedJson].ShouldBe(1);
        report.InvalidByReason[RejectionReason.UnknownMetric].ShouldBe(1);
        report.InvalidByReason[RejectionReason.MissingField].ShouldBe(1);
        report.InvalidByReason[RejectionReason.InvalidTimestamp].ShouldBe(1);
        report.FileDuplicates.ShouldBe(2);
        report.NewlyStored.ShouldBe(3);
        report.AlreadyStored.ShouldBe(0);
        report.Acceptable.ShouldBe(2);
        report.Unacceptable.ShouldBe(1);
        report.RulesLoadedTotal.ShouldBe(1);
        report.RulesLoadedEnabled.ShouldBe(1);
        report.RuleEvaluations.ShouldBe(3);
        report.RuleViolations.ShouldBe(1);
        report.BrokenInvariants().ShouldBeEmpty();
        h.Readings.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Invalid_and_duplicate_readings_never_reach_evaluation_or_storage()
    {
        var h = WithRules(Harness.GreaterThan("R1", "temperature", -1000));
        var lines = new List<RawLine>
        {
            Lines.Temp("A", Lines.At(0), 50, 1),
            Lines.Malformed(),
            Lines.WithRaw(Field.Str("A"), Field.Str("temperature"), Field.Str(Lines.At(0)), Field.Str("12"), Field.Int(9)),
        };

        var report = await h.RunAsync(lines);

        report.RuleEvaluations.ShouldBe(1);
        h.Readings.Count.ShouldBe(1);
        h.RuleResults.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sustained_episode_persists_unacceptable_classifications_results_and_one_alert()
    {
        var h = WithRules(Harness.Sustained("S1", "temperature", 80, 30));

        var report = await h.RunAsync(EpisodeFile());

        report.Unacceptable.ShouldBe(5);
        report.Acceptable.ShouldBe(4);
        report.AlertsGenerated.ShouldBe(1);
        report.AlertsSuppressed.ShouldBe(0);
        report.RuleViolations.ShouldBe(5);
        h.RuleResults.Count.ShouldBe(5);
        h.Alerts.All.Single().PeakValue.ShouldBe(89);
        h.UnitOfWork.Executions.ShouldBe(1);
    }

    [Fact]
    public async Task A_shuffled_file_gives_identical_stored_readings_results_and_alerts()
    {
        var lines = EpisodeFile("PUMP-01").Concat(EpisodeFile("PUMP-02")).ToList();
        lines.Add(Lines.Temp("PUMP-03", Lines.At(0), 120, 100));
        lines.Add(Lines.Temp("PUMP-03", Lines.At(0), 130, 100)); // conflicting duplicate in the file
        lines.Add(Lines.Temp("PUMP-03", Lines.At(0), 125, 100)); // another copy
        lines.Add(Lines.Blank());
        lines.Add(Lines.Malformed());
        var rules = new[] { Harness.Sustained("S1", "temperature", 80, 30), Harness.GreaterThan("R1", "temperature", 122) };

        var baseline = new Harness { Rules = Harness.BuildRules(rules) };
        await baseline.RunAsync(lines);

        foreach (var seed in new[] { 3, 11, 29, 2025 })
        {
            var shuffled = new Harness { Rules = Harness.BuildRules(rules) };
            await shuffled.RunAsync(Lines.Shuffled(lines, seed));

            shuffled.Readings.Snapshot().ShouldBe(baseline.Readings.Snapshot());
            shuffled.RuleResults.Snapshot().ShouldBe(baseline.RuleResults.Snapshot());
            shuffled.Alerts.Snapshot().ShouldBe(baseline.Alerts.Snapshot());
        }

        baseline.Readings.ValueOf(new ReadingKey(new DeviceId("PUMP-03"), Metric.Temperature, new System.DateTimeOffset(2025, 6, 1, 8, 0, 0, System.TimeSpan.Zero), 100)).ShouldBe(120);
    }

    [Fact]
    public async Task Re_running_the_same_file_changes_nothing_and_reports_everything_as_already_stored()
    {
        var h = WithRules(Harness.Sustained("S1", "temperature", 80, 30), Harness.GreaterThan("R1", "temperature", 86));
        var lines = EpisodeFile();

        var first = await h.RunAsync(lines);
        var readings = h.Readings.Snapshot();
        var results = h.RuleResults.Snapshot();
        var alerts = h.Alerts.Snapshot();
        var second = await h.RunAsync(lines);

        h.Readings.Snapshot().ShouldBe(readings);
        h.RuleResults.Snapshot().ShouldBe(results);
        h.Alerts.Snapshot().ShouldBe(alerts);
        second.NewlyStored.ShouldBe(0);
        second.AlreadyStored.ShouldBe(first.NewlyStored);
        second.Acceptable.ShouldBe(first.Acceptable);
        second.Unacceptable.ShouldBe(first.Unacceptable);
        second.AlertsGenerated.ShouldBe(first.AlertsGenerated);
        second.RuleViolations.ShouldBe(first.RuleViolations);
        second.BrokenInvariants().ShouldBeEmpty();
        first.BrokenInvariants().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_later_file_with_earlier_timestamps_re_evaluates_the_series_and_leaves_no_orphan_alert()
    {
        var h = WithRules(Harness.Sustained("S1", "temperature", 80, 30));
        // First file: above from second 40 to 70 (ending reading at 80) => alert starting at 40.
        await h.RunAsync(new[]
        {
            Lines.Temp("A", Lines.At(30), 70, 1), Lines.Temp("A", Lines.At(40), 90, 2), Lines.Temp("A", Lines.At(50), 90, 3),
            Lines.Temp("A", Lines.At(60), 90, 4), Lines.Temp("A", Lines.At(70), 90, 5), Lines.Temp("A", Lines.At(80), 70, 6),
        });
        h.Alerts.All.Single().StartTs.ShouldBe(new System.DateTimeOffset(2025, 6, 1, 8, 0, 40, System.TimeSpan.Zero));

        // Second file adds a LATE-ARRIVING reading with an earlier timestamp (second 35) that is above the threshold.
        var second = await h.RunAsync(new[] { Lines.Temp("A", Lines.At(35), 91, 7) });

        // The series is re-evaluated from all stored readings: the episode now starts at 35 and the old alert is replaced.
        h.Alerts.All.Count.ShouldBe(1);
        h.Alerts.All.Single().StartTs.ShouldBe(new System.DateTimeOffset(2025, 6, 1, 8, 0, 35, System.TimeSpan.Zero));
        second.NewlyStored.ShouldBe(1);
        second.AlreadyStored.ShouldBe(0);
        second.BrokenInvariants().ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_a_rule_between_runs_removes_its_stale_results()
    {
        var h = WithRules(Harness.GreaterThan("R1", "temperature", 100));
        var lines = new List<RawLine> { Lines.Temp("A", Lines.At(0), 150, 1) };
        await h.RunAsync(lines);
        h.RuleResults.Count.ShouldBe(1);

        h.Rules = Harness.BuildRules();
        var report = await h.RunAsync(lines);

        h.RuleResults.Count.ShouldBe(0);
        report.Unacceptable.ShouldBe(0);
        report.Acceptable.ShouldBe(1);
        h.Readings.Snapshot().Single().ShouldEndWith("Acceptable");
    }

    [Fact]
    public async Task A_disabled_rule_produces_no_results_and_no_evaluations()
    {
        var h = WithRules(Harness.GreaterThan("R1", "temperature", 0, enabled: false));

        var report = await h.RunAsync(new[] { Lines.Temp("A", Lines.At(0), 150, 1) });

        report.RuleEvaluations.ShouldBe(0);
        report.RulesLoadedTotal.ShouldBe(1);
        report.RulesLoadedEnabled.ShouldBe(0);
        h.RuleResults.Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_reading_already_stored_with_a_different_value_keeps_the_stored_value_and_warns()
    {
        var h = WithRules(Harness.GreaterThan("R1", "temperature", 100));
        await h.RunAsync(new[] { Lines.Temp("A", Lines.At(0), 150, 1) });

        var report = await h.RunAsync(new[] { Lines.Temp("A", Lines.At(0), 50, 1) });

        report.NewlyStored.ShouldBe(0);
        report.AlreadyStored.ShouldBe(1);
        report.Unacceptable.ShouldBe(1); // stored value 150 still evaluated
        h.Readings.Snapshot().Single().ShouldContain("|150|");
        h.Logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("already stored", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_empty_file_produces_an_all_zero_report_without_error()
    {
        var h = WithRules(Harness.GreaterThan("R1", "temperature", 100));

        var report = await h.RunAsync(new List<RawLine>());

        report.LinesRead.ShouldBe(0);
        report.NewlyStored.ShouldBe(0);
        report.BrokenInvariants().ShouldBeEmpty();
        h.Sink.Written.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Only_series_present_in_the_file_are_re_evaluated()
    {
        var h = WithRules(Harness.Sustained("S1", "temperature", 80, 30));
        await h.RunAsync(EpisodeFile("PUMP-01"));
        var before = h.Alerts.Snapshot();

        var report = await h.RunAsync(EpisodeFile("PUMP-02"));

        h.Alerts.Snapshot().Count.ShouldBe(2);
        h.Alerts.Snapshot().ShouldContain(before.Single());
        report.AlertsGenerated.ShouldBe(1); // only PUMP-02 was evaluated in this run
    }
}
