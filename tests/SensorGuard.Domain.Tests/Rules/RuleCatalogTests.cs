using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Rules;
using SensorGuard.Domain.Tests.Support;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Rules;

public sealed class RuleCatalogTests
{
    private readonly RuleCatalog _catalog = new(TestRules.Registry());

    private static RuleDefinition Def(
        string id = "R1",
        string name = "Overheating",
        bool enabled = true,
        string metric = "temperature",
        string? device = null,
        string op = "GreaterThan",
        params (string, double)[] parameters) =>
        new(id, name, enabled, metric, device, op, parameters.Length == 0
            ? new Dictionary<string, double> { ["value"] = 100 }
            : parameters.ToDictionary(p => p.Item1, p => p.Item2));

    private string BuildError(params RuleDefinition[] definitions) =>
        Should.Throw<RuleValidationException>(() => _catalog.Build(definitions)).Message;

    [Fact]
    public void Valid_definitions_become_rules_with_parsed_metric_device_and_kind()
    {
        var rules = _catalog.Build(new[]
        {
            Def("R1"),
            Def("R2", device: "PUMP-01", metric: "pressure", op: "Between", parameters: new[] { ("min", 0.0), ("max", 12.0) }),
        });

        rules.Count.ShouldBe(2);
        rules[0].Id.ShouldBe(new RuleId("R1"));
        rules[0].Metric.ShouldBe(Metric.Temperature);
        rules[0].Device.ShouldBeNull();
        rules[0].Kind.ShouldBe(OperatorKind.Stateless);
        rules[1].Device.ShouldBe(new DeviceId("PUMP-01"));
        rules[1].OperatorName.ShouldBe("Between");
    }

    [Fact]
    public void Operator_names_match_case_insensitively_and_are_stored_canonically()
    {
        var rules = _catalog.Build(new[] { Def(op: "greaterTHAN") });

        rules.Single().OperatorName.ShouldBe("GreaterThan");
    }

    [Fact]
    public void Disabled_rules_are_kept_as_data_but_flagged_disabled()
    {
        var rules = _catalog.Build(new[] { Def(enabled: false) });

        rules.Single().Enabled.ShouldBeFalse();
    }

    [Fact]
    public void A_rule_for_a_device_with_no_data_is_valid()
    {
        Should.NotThrow(() => _catalog.Build(new[] { Def(device: "NO-SUCH-DEVICE") }));
    }

    [Fact]
    public void Duplicate_rule_ids_fail_and_name_the_id()
    {
        var message = BuildError(Def("R1"), Def("R1", name: "Other"));

        message.ShouldContain("R1");
        message.ShouldContain("duplicate", Case.Insensitive);
    }

    [Fact]
    public void Unknown_operator_fails_and_names_the_rule_and_the_operator()
    {
        var message = BuildError(Def("R7", name: "Weird rule", op: "Nope"));

        message.ShouldContain("R7");
        message.ShouldContain("Weird rule");
        message.ShouldContain("Nope");
    }

    [Fact]
    public void Unknown_metric_fails_and_names_the_rule()
    {
        var message = BuildError(Def("R3", metric: "humidity"));

        message.ShouldContain("R3");
        message.ShouldContain("humidity");
    }

    [Fact]
    public void Wrong_case_metric_fails()
    {
        BuildError(Def("R3", metric: "Temperature")).ShouldContain("R3");
    }

    [Fact]
    public void A_missing_operator_parameter_fails_and_names_the_rule()
    {
        var message = BuildError(Def("R4", parameters: new[] { ("min", 1.0) }));

        message.ShouldContain("R4");
        message.ShouldContain("value");
    }

    [Fact]
    public void Between_with_min_above_max_fails()
    {
        var message = BuildError(Def("R5", op: "Between", parameters: new[] { ("min", 9.0), ("max", 1.0) }));

        message.ShouldContain("R5");
        message.ShouldContain("min");
    }

    [Fact]
    public void Non_finite_parameters_fail()
    {
        BuildError(Def("R6", parameters: new[] { ("value", double.NaN) })).ShouldContain("R6");
    }

    [Fact]
    public void Empty_id_or_name_or_device_text_fails()
    {
        BuildError(Def(id: " ")).ShouldContain("id");
        BuildError(Def(name: "")).ShouldContain("name");
        BuildError(Def(device: " ")).ShouldContain("deviceId");
    }

    [Fact]
    public void All_problems_are_reported_together()
    {
        var ex = Should.Throw<RuleValidationException>(() => _catalog.Build(new[]
        {
            Def("A", op: "Nope"),
            Def("B", metric: "humidity"),
        }));

        ex.Errors.Count.ShouldBe(2);
        ex.Message.ShouldContain("A");
        ex.Message.ShouldContain("B");
    }
}
