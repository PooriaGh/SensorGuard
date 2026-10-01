using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace SensorGuard.Api.Tests;

/// <summary>US2/AC6 and research R6: bad rules (or unreadable input) stop startup with a clear message, before the database is touched.</summary>
public sealed class StartupFailFastTests : IDisposable
{
    private const string ValidInput =
        "{\"deviceId\": \"D1\", \"metric\": \"temperature\", \"ts\": \"2025-06-01T08:00:00Z\", \"value\": 70, \"seq\": 1}\n";

    private const string ValidRules =
        "[ { \"id\": \"R1\", \"name\": \"Too hot\", \"enabled\": true, \"metric\": \"temperature\", \"operator\": \"GreaterThan\", \"value\": 100 } ]";

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sensorguard-startup-" + Guid.NewGuid().ToString("N"));

    public StartupFailFastTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private ServiceProvider Services(string inputPath, string rulesPath, string databasePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SensorGuard:InputFilePath"] = inputPath,
                ["SensorGuard:RulesFilePath"] = rulesPath,
                ["SensorGuard:DatabasePath"] = databasePath,
            })
            .Build();
        return new ServiceCollection().AddLogging().AddSensorGuard(configuration).BuildServiceProvider();
    }

    [Fact]
    public async Task Invalid_rules_stop_startup_name_every_problem_and_never_create_the_database()
    {
        var database = Path.Combine(_dir, "never.db");
        var rules = Write("bad.json", """
            [
              { "id": "R1", "name": "Fine", "enabled": true, "metric": "temperature", "operator": "GreaterThan", "value": 100 },
              { "id": "R7", "name": "Weird rule", "enabled": true, "metric": "temperature", "operator": "Nope", "value": 1 },
              { "id": "R1", "name": "Dup", "enabled": true, "metric": "humidity", "operator": "Between", "min": 9, "max": 1 }
            ]
            """);
        var logger = new CapturingLogger();
        await using var services = Services(Write("in.json", ValidInput), rules, database);

        var report = await Startup.RunIngestionAsync(services, logger, CancellationToken.None);

        report.ShouldBeNull();
        File.Exists(database).ShouldBeFalse();
        var message = logger.Entries.Single(e => e.Level == LogLevel.Critical).Message;
        message.ShouldContain("R7");
        message.ShouldContain("Nope");
        message.ShouldContain("duplicate rule id 'R1'");
        message.ShouldContain("humidity");
        message.ShouldContain("min must not be greater than max");
    }

    [Fact]
    public async Task A_missing_rules_file_stops_startup_naming_the_file_before_the_database_is_created()
    {
        var database = Path.Combine(_dir, "never.db");
        var logger = new CapturingLogger();
        await using var services = Services(Write("in.json", ValidInput), Path.Combine(_dir, "missing-rules.json"), database);

        var report = await Startup.RunIngestionAsync(services, logger, CancellationToken.None);

        report.ShouldBeNull();
        File.Exists(database).ShouldBeFalse();
        logger.Entries.Single(e => e.Level == LogLevel.Critical).Message.ShouldContain("missing-rules.json");
    }

    [Fact]
    public async Task A_missing_input_file_stops_startup_with_a_clear_message()
    {
        var logger = new CapturingLogger();
        await using var services = Services(Path.Combine(_dir, "missing-input.json"), Write("rules.json", ValidRules), Path.Combine(_dir, "x.db"));

        var report = await Startup.RunIngestionAsync(services, logger, CancellationToken.None);

        report.ShouldBeNull();
        var message = logger.Entries.Single(e => e.Level == LogLevel.Critical).Message;
        message.ShouldContain("input could not be read");
        message.ShouldContain("missing-input.json");
    }

    [Fact]
    public async Task Valid_rules_and_input_complete_startup_with_a_reconciled_report()
    {
        var logger = new CapturingLogger();
        await using var services = Services(Write("in.json", ValidInput), Write("rules.json", ValidRules), Path.Combine(_dir, "ok.db"));

        var report = await Startup.RunIngestionAsync(services, logger, CancellationToken.None);

        report.ShouldNotBeNull();
        report.NewlyStored.ShouldBe(1);
        report.BrokenInvariants().ShouldBeEmpty();
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Critical);
    }
}
