using System;
using System.Collections.Generic;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Rules;
using SensorGuard.Domain.Rules.Operators;
using SensorGuard.Domain.Tests.Support;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Rules;

public sealed class OperatorRegistryTests
{
    private sealed class IsOddOperator : IStatelessOperator
    {
        public string Name => "IsOdd";

        public IReadOnlyList<string> Validate(RuleParameters parameters) => Array.Empty<string>();

        public bool IsViolated(double value, Rule rule) => Math.Abs(value % 2) == 1;

        public string Describe(double value, Rule rule) => $"{value} is odd";
    }

    [Fact]
    public void Lookup_is_case_insensitive_and_returns_null_for_unknown_names()
    {
        var registry = TestRules.Registry();

        registry.FindStateless("greaterthan").ShouldBeOfType<GreaterThanOperator>();
        registry.FindStateless("BETWEEN").ShouldBeOfType<BetweenOperator>();
        registry.FindStateless("Nope").ShouldBeNull();
        registry.FindStateful("GreaterThan").ShouldBeNull();
    }

    [Fact]
    public void Duplicate_operator_names_are_rejected_at_construction()
    {
        Should.Throw<InvalidOperationException>(
            () => TestRules.Registry(extraStateless: new IStatelessOperator[] { new GreaterThanOperator() }));
    }

    [Fact]
    public void A_new_operator_is_one_class_plus_one_registration_with_no_change_to_evaluation_code()
    {
        var registry = TestRules.Registry(extraStateless: new IStatelessOperator[] { new IsOddOperator() });
        var catalog = new RuleCatalog(registry);

        var rules = catalog.Build(new[]
        {
            new RuleDefinition("X1", "Odd readings", true, "pressure", null, "isodd", new Dictionary<string, double>()),
        });
        var result = TestRules.Evaluator(registry).Evaluate(
            new[]
            {
                Readings.Pressure("d", "2025-06-01T08:00:00Z", 1, 3),
                Readings.Pressure("d", "2025-06-01T08:00:10Z", 2, 4),
            },
            rules);

        result.Violations.Count.ShouldBe(1);
        result.Violations[0].Reason.ShouldBe("3 is odd");
        result.Violations[0].Rule.ShouldBe(new RuleId("X1"));
    }
}
