using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SensorGuard.Domain.Rules;
using SensorGuard.Infrastructure.Files;
using Shouldly;
using Xunit;

namespace SensorGuard.Infrastructure.Tests.Files;

public sealed class JsonRuleSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sensorguard-rules-" + Guid.NewGuid().ToString("N"));

    public JsonRuleSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<System.Collections.Generic.IReadOnlyList<RuleDefinition>> LoadAsync(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, json);
        return await new JsonRuleSource(path).LoadAsync(default);
    }

    private async Task<string> LoadErrorAsync(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, json);
        var ex = await Should.ThrowAsync<RuleSourceException>(() => new JsonRuleSource(path).LoadAsync(default));
        return ex.Message;
    }

    [Fact]
    public async Task Loads_the_documented_shape_with_flat_numeric_parameters()
    {
        var rules = await LoadAsync("""
            [
              { "id": "R1", "name": "Overheating", "enabled": true, "metric": "temperature", "operator": "GreaterThan", "value": 100 },
              { "id": "R2", "name": "Band", "enabled": true, "metric": "pressure", "deviceId": "PUMP-01", "operator": "between", "min": 1.0, "max": 8.0 },
              { "id": "R3", "name": "Sustained", "enabled": false, "metric": "temperature", "operator": "SustainedAbove", "threshold": 80, "durationSeconds": 30 }
            ]
            """);

        rules.Count.ShouldBe(3);
        rules[0].Id.ShouldBe("R1");
        rules[0].Enabled.ShouldBeTrue();
        rules[0].DeviceId.ShouldBeNull();
        rules[0].Parameters["value"].ShouldBe(100);
        rules[1].DeviceId.ShouldBe("PUMP-01");
        rules[1].Operator.ShouldBe("between");
        rules[1].Parameters["min"].ShouldBe(1.0);
        rules[1].Parameters["max"].ShouldBe(8.0);
        rules[2].Enabled.ShouldBeFalse();
        rules[2].Parameters.Keys.OrderBy(k => k).ShouldBe(new[] { "durationSeconds", "threshold" });
    }

    [Fact]
    public async Task Loads_the_shipped_seed_file()
    {
        var path = FindSeedFile();

        var rules = await new JsonRuleSource(path).LoadAsync(default);

        rules.Count.ShouldBeGreaterThan(0);
        rules.Select(r => r.Id).Distinct().Count().ShouldBe(rules.Count);
    }

    [Fact]
    public async Task A_missing_file_fails_with_a_clear_message()
    {
        var ex = await Should.ThrowAsync<RuleSourceException>(
            () => new JsonRuleSource(Path.Combine(_dir, "nope.json")).LoadAsync(default));

        ex.Message.ShouldContain("nope.json");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"id\": \"R1\" }")]
    [InlineData("42")]
    public async Task A_file_that_is_not_a_json_array_of_objects_fails_clearly(string json)
    {
        (await LoadErrorAsync(json)).ShouldContain("array");
    }

    [Fact]
    public async Task A_non_numeric_operator_parameter_fails_and_names_the_rule()
    {
        var message = await LoadErrorAsync("""
            [ { "id": "R9", "name": "Bad", "enabled": true, "metric": "temperature", "operator": "GreaterThan", "value": "100" } ]
            """);

        message.ShouldContain("R9");
        message.ShouldContain("value");
    }

    [Fact]
    public async Task Missing_text_fields_are_loaded_as_empty_so_the_catalog_reports_them()
    {
        var rules = await LoadAsync("""[ { "enabled": true, "metric": "temperature", "operator": "GreaterThan", "value": 1 } ]""");

        rules.Single().Id.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task A_missing_enabled_flag_defaults_to_enabled()
    {
        var rules = await LoadAsync("""[ { "id": "R1", "name": "n", "metric": "temperature", "operator": "GreaterThan", "value": 1 } ]""");

        rules.Single().Enabled.ShouldBeTrue();
    }

    private static string FindSeedFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SensorGuard.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        return Path.Combine(dir.FullName, "data", "rules.json");
    }
}
