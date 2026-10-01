using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SensorGuard.Application.Ports;
using SensorGuard.Application.Tests.Support;
using SensorGuard.Domain.Ingestion;
using Shouldly;
using Xunit;

namespace SensorGuard.Application.Tests;

public sealed class ProcessReadingsFileLoggingTests
{
    private static List<RawLine> EpisodeFile(string device, int offsetSeconds = 0, long seqStart = 1) =>
        new[] { 70.0, 71, 85, 86, 87, 88, 89, 70, 70 }
            .Select((v, i) => Lines.Temp(device, Lines.At(offsetSeconds + (i * 10)), v, seqStart + i))
            .ToList();

    private static Harness WithSustainedRule() =>
        new() { Rules = Harness.BuildRules(Harness.Sustained("S1", "temperature", 80, 30)) };

    [Fact]
    public async Task Rejected_lines_and_conflicting_duplicates_log_at_Warning()
    {
        var h = WithSustainedRule();

        await h.RunAsync(new List<RawLine>
        {
            Lines.Malformed(),
            Lines.Temp("A", Lines.At(0), 70, 1),
            Lines.Temp("A", Lines.At(0), 65, 1),
        });

        var warnings = h.Logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        warnings.ShouldContain(m => m.Contains("Line 1 rejected") && m.Contains("MalformedJson"));
        warnings.ShouldContain(m => m.Contains("Conflicting duplicate") && m.Contains("kept 65"));
    }

    [Fact]
    public async Task Rules_loaded_episodes_alerts_suppressions_and_the_report_log_at_Information()
    {
        var h = WithSustainedRule();
        // Second episode starts 2 minutes after the first: inside the 5 minute cooldown.
        var lines = EpisodeFile("A").Concat(new[] { 70.0, 70, 70, 70, 70, 70 }.Select((v, i) => Lines.Temp("A", Lines.At(100 + (i * 10)), v, 100 + i)))
            .Concat(new[] { 85.0, 85, 85, 85, 70 }.Select((v, i) => Lines.Temp("A", Lines.At(160 + (i * 10)), v, 200 + i)))
            .ToList();

        await h.RunAsync(lines);

        var info = h.Logger.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message).ToList();
        info.ShouldContain(m => m.StartsWith("Loaded 1 rules (1 enabled)"));
        info.ShouldContain(m => m.StartsWith("Sustained episode for rule S1"));
        info.ShouldContain(m => m.StartsWith("Alert raised for rule S1"));
        info.ShouldContain(m => m.StartsWith("Alert suppressed by cooldown for rule S1"));
        info.ShouldContain(m => m.StartsWith("Processing report:"));
    }

    [Fact]
    public async Task Valid_readings_do_not_log_anything_per_reading_at_Information()
    {
        var h = new Harness { Rules = Harness.BuildRules(Harness.GreaterThan("R1", "temperature", 1000)) };
        var lines = Enumerable.Range(0, 200).Select(i => Lines.Temp("A", Lines.At(i * 10), 50 + i, i + 1)).ToList();

        await h.RunAsync(lines);

        // Rules loaded + the report: nothing proportional to the number of readings.
        h.Logger.Entries.Count(e => e.Level == LogLevel.Information).ShouldBeLessThan(5);
        h.Logger.Entries.ShouldNotContain(e => e.Message.Contains("classified"));
    }

    [Fact]
    public async Task Per_reading_detail_is_logged_only_at_Debug_and_only_when_debug_is_enabled()
    {
        var h = new Harness { Rules = Harness.BuildRules(Harness.GreaterThan("R1", "temperature", 1000)) };
        h.Logger.MinimumLevel = LogLevel.Debug;

        await h.RunAsync(new[] { Lines.Temp("A", Lines.At(0), 50, 1), Lines.Temp("A", Lines.At(10), 51, 2) });

        var detail = h.Logger.Entries.Where(e => e.Message.Contains("classified")).ToList();
        detail.Count.ShouldBe(2);
        detail.All(e => e.Level == LogLevel.Debug).ShouldBeTrue();
    }

    [Fact]
    public async Task The_report_is_written_to_the_sink_once_per_run_and_matches_the_formatter()
    {
        var h = WithSustainedRule();

        var report = await h.RunAsync(EpisodeFile("A"));

        h.Sink.Written.Count.ShouldBe(1);
        h.Sink.Written[0].ShouldBe(ReportFormatter.Format(report));
    }

    [Fact]
    public async Task A_consistent_run_logs_no_invariant_warning()
    {
        var h = WithSustainedRule();

        await h.RunAsync(EpisodeFile("A"));

        h.Logger.Entries.ShouldNotContain(e => e.Message.Contains("invariant"));
    }
}
