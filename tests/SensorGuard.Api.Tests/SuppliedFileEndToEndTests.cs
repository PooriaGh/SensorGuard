using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Domain.Reporting;
using Shouldly;
using Xunit;

namespace SensorGuard.Api.Tests;

/// <summary>
/// End to end on the supplied sensor-data.json and the seed rules.json (fixtures copied from data/). The expected numbers
/// come from an independent simulation of the specification, recorded in specs/001-sensor-ingestion-alerting/research.md R15.
/// If one disagrees, work out which side is wrong before changing either.
/// </summary>
public sealed class SuppliedFileEndToEndTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static ProcessingReport ReportOf(ApiFactory factory)
    {
        _ = factory.Server; // starts the host, which ingests the file at startup
        return factory.Services.GetRequiredService<StartupResult>().Report!;
    }

    private static List<string> Rows(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        SqliteConnection.ClearAllPools();
        return rows;
    }

    private const string AlertsSql = "select rule_id, device_id, metric, start_ts_utc, end_ts_utc, peak_value, open_at_end from alerts order by rule_id, device_id, metric, start_ts_utc";

    [Fact]
    public void The_report_reconciles_and_matches_the_reference_numbers_for_the_supplied_file()
    {
        using var factory = new ApiFactory(Fixture("sensor-data.json"), Fixture("rules.json"));

        var report = ReportOf(factory);

        report.LinesRead.ShouldBe(2150);
        report.BlankLines.ShouldBe(0);
        report.Parsed.ShouldBe(2141);
        report.InvalidRejected.ShouldBe(9);
        report.InvalidByReason[RejectionReason.MissingField].ShouldBe(5);
        report.InvalidByReason[RejectionReason.MalformedJson].ShouldBe(1);
        report.InvalidByReason[RejectionReason.InvalidType].ShouldBe(1);
        report.InvalidByReason[RejectionReason.InvalidTimestamp].ShouldBe(2);
        report.FileDuplicates.ShouldBe(38);
        report.AlreadyStored.ShouldBe(0);
        report.NewlyStored.ShouldBe(2103);
        report.RulesLoadedTotal.ShouldBe(11);
        report.RulesLoadedEnabled.ShouldBe(10);
        report.RuleEvaluations.ShouldBe(3371);
        report.Acceptable.ShouldBe(1812);
        report.Unacceptable.ShouldBe(291);
        report.RuleViolations.ShouldBe(292);
        report.AlertsGenerated.ShouldBe(5);
        report.AlertsSuppressed.ShouldBe(4);
        report.BrokenInvariants().ShouldBeEmpty();
    }

    [Fact]
    public void Each_sustained_episode_in_the_data_produces_one_alert_with_the_reference_start_and_end_times()
    {
        using var factory = new ApiFactory(Fixture("sensor-data.json"), Fixture("rules.json"));
        _ = ReportOf(factory);

        var alerts = Rows(factory.DatabasePath, AlertsSql);

        alerts.ShouldBe(new[]
        {
            "R08|COMP-01|temperature|2025-06-01T08:11:50.0000000Z|2025-06-01T08:13:50.0000000Z|71.789|0",
            "R08|FAN-03|temperature|2025-06-01T08:28:10.0000000Z|2025-06-01T08:29:40.0000000Z|71.414|0",
            "R08|PUMP-02|temperature|2025-06-01T08:22:30.0000000Z|2025-06-01T08:24:10.0000000Z|71.542|0",
            "R09|PUMP-02|pressure|2025-06-01T08:18:40.0000000Z|2025-06-01T08:29:40.0000000Z|11.088|0",
            "R10|FAN-03|vibration|2025-06-01T08:26:50.0000000Z|2025-06-01T08:28:10.0000000Z|6.278|0",
        });
    }

    [Fact]
    public void Re_running_against_the_same_database_creates_no_new_data_and_reports_everything_as_already_stored()
    {
        using var first = new ApiFactory(Fixture("sensor-data.json"), Fixture("rules.json"));
        var firstReport = ReportOf(first);
        var before = (
            Rows(first.DatabasePath, "select count(*) from readings"),
            Rows(first.DatabasePath, "select count(*) from rule_results"),
            Rows(first.DatabasePath, AlertsSql),
            Rows(first.DatabasePath, "select device_id, metric, ts_utc, seq, value, classification from readings order by device_id, metric, ts_utc, seq"));

        using var second = new ApiFactory(Fixture("sensor-data.json"), Fixture("rules.json"), first.DatabasePath);
        var secondReport = ReportOf(second);

        secondReport.NewlyStored.ShouldBe(0);
        secondReport.AlreadyStored.ShouldBe(firstReport.NewlyStored);
        secondReport.Acceptable.ShouldBe(firstReport.Acceptable);
        secondReport.Unacceptable.ShouldBe(firstReport.Unacceptable);
        secondReport.AlertsGenerated.ShouldBe(firstReport.AlertsGenerated);
        secondReport.BrokenInvariants().ShouldBeEmpty();
        Rows(first.DatabasePath, "select count(*) from readings").ShouldBe(before.Item1);
        Rows(first.DatabasePath, "select count(*) from rule_results").ShouldBe(before.Item2);
        Rows(first.DatabasePath, AlertsSql).ShouldBe(before.Item3);
        Rows(first.DatabasePath, "select device_id, metric, ts_utc, seq, value, classification from readings order by device_id, metric, ts_utc, seq").ShouldBe(before.Item4);
    }

    [Fact]
    public void A_shuffled_copy_of_the_file_gives_identical_classifications_and_alerts()
    {
        var lines = Fixture("sensor-data.json").Split('\n').Where(l => l.Length > 0).ToList();
        var rng = new Random(20251);
        for (var i = lines.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (lines[i], lines[j]) = (lines[j], lines[i]);
        }

        using var original = new ApiFactory(Fixture("sensor-data.json"), Fixture("rules.json"));
        using var shuffled = new ApiFactory(string.Join('\n', lines), Fixture("rules.json"));
        _ = ReportOf(original);
        var shuffledReport = ReportOf(shuffled);

        const string readings = "select device_id, metric, ts_utc, seq, value, classification from readings order by device_id, metric, ts_utc, seq";
        const string results = "select device_id, metric, ts_utc, seq, rule_id, reason from rule_results order by device_id, metric, ts_utc, seq, rule_id";
        Rows(shuffled.DatabasePath, readings).ShouldBe(Rows(original.DatabasePath, readings));
        Rows(shuffled.DatabasePath, results).ShouldBe(Rows(original.DatabasePath, results));
        Rows(shuffled.DatabasePath, AlertsSql).ShouldBe(Rows(original.DatabasePath, AlertsSql));
        shuffledReport.BrokenInvariants().ShouldBeEmpty();
    }

    [Fact]
    public async Task Aggregation_over_the_supplied_data_excludes_unacceptable_readings()
    {
        using var factory = new ApiFactory(Fixture("sensor-data.json"), Fixture("rules.json"));
        _ = ReportOf(factory);
        using var client = factory.CreateClient();

        // FAN-03 temperature holds one unacceptable reading of 1,000,000 (rule R01). It must not distort the maximum.
        var response = await client.GetAsync("/api/aggregations?deviceId=FAN-03&metric=temperature&from=2025-06-01T08:00:00Z&to=2025-06-01T08:35:00Z&bucketSeconds=300");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var buckets = doc.RootElement.GetProperty("buckets").EnumerateArray().ToList();
        buckets.Count.ShouldBe(7);
        buckets.Max(b => b.GetProperty("max").GetDouble()).ShouldBeLessThan(100);
        buckets.Sum(b => b.GetProperty("count").GetInt32()).ShouldBeGreaterThan(150);
    }
}
