using System.Linq;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Tests.Support;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Evaluation;

public sealed class SeriesEvaluatorStatelessTests
{
    private readonly SensorGuard.Domain.Evaluation.SeriesEvaluator _evaluator = TestRules.Evaluator();

    [Fact]
    public void A_reading_with_no_applicable_rule_is_acceptable()
    {
        var rules = new[] { TestRules.Stateless("R1", "GreaterThan", Metric.Pressure, parameters: new (string, double)[] { ("value", 1) }) };

        var result = _evaluator.Evaluate(new[] { Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 5000) }, rules);

        result.Classifications.Single().Classification.ShouldBe(Classification.Acceptable);
        result.Violations.ShouldBeEmpty();
        result.RuleEvaluations.ShouldBe(0);
    }

    [Fact]
    public void A_violated_rule_makes_the_reading_unacceptable_and_names_the_rule_with_a_reason()
    {
        var rules = new[] { TestRules.Stateless("R1", "GreaterThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 100) }) };

        var result = _evaluator.Evaluate(new[] { Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 103.2) }, rules);

        var classified = result.Classifications.Single();
        classified.Classification.ShouldBe(Classification.Unacceptable);
        classified.Violations.Single().Rule.ShouldBe(new RuleId("R1"));
        classified.Violations.Single().Reason.ShouldBe("temperature GreaterThan 100 fires at 103.2");
    }

    [Fact]
    public void All_violated_rules_are_reported()
    {
        var rules = new[]
        {
            TestRules.Stateless("R1", "GreaterThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 100) }),
            TestRules.Stateless("R2", "GreaterThanOrEqual", Metric.Temperature, parameters: new (string, double)[] { ("value", 105) }),
            TestRules.Stateless("R3", "LessThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 0) }),
        };

        var result = _evaluator.Evaluate(new[] { Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 105) }, rules);

        result.Classifications.Single().Violations.Select(v => v.Rule.Value).ShouldBe(new[] { "R1", "R2" });
        result.Violations.Count.ShouldBe(2);
    }

    [Fact]
    public void A_disabled_rule_is_never_evaluated()
    {
        var rules = new[] { TestRules.Stateless("R1", "GreaterThan", Metric.Temperature, enabled: false, parameters: new (string, double)[] { ("value", 0) }) };

        var result = _evaluator.Evaluate(new[] { Readings.Temp("d", "2025-06-01T08:00:00Z", 1, 50) }, rules);

        result.Violations.ShouldBeEmpty();
        result.RuleEvaluations.ShouldBe(0);
    }

    [Fact]
    public void A_device_rule_only_evaluates_that_devices_readings()
    {
        var rules = new[] { TestRules.Stateless("R1", "GreaterThan", Metric.Temperature, device: "PUMP-01", parameters: new (string, double)[] { ("value", 0) }) };

        var result = _evaluator.Evaluate(
            new[]
            {
                Readings.Temp("PUMP-01", "2025-06-01T08:00:00Z", 1, 5),
                Readings.Temp("PUMP-02", "2025-06-01T08:00:00Z", 1, 5),
            },
            rules);

        result.Violations.Select(v => v.Reading.Device.Value).ShouldBe(new[] { "PUMP-01" });
        result.RuleEvaluations.ShouldBe(1);
    }

    [Fact]
    public void Rule_evaluations_equal_readings_times_applicable_enabled_stateless_rules()
    {
        var rules = new[]
        {
            TestRules.Stateless("R1", "GreaterThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 1000) }),
            TestRules.Stateless("R2", "LessThan", Metric.Temperature, parameters: new (string, double)[] { ("value", -1000) }),
            TestRules.Stateless("R3", "LessThan", Metric.Pressure, parameters: new (string, double)[] { ("value", -1000) }),
        };

        var result = _evaluator.Evaluate(
            Readings.Series("d", Metric.Temperature, "2025-06-01T08:00:00Z", 10, 1, 2, 3, 4),
            rules);

        result.RuleEvaluations.ShouldBe(8);
        result.Classifications.Count.ShouldBe(4);
    }

    [Fact]
    public void Every_reading_is_classified_in_series_then_event_time_order()
    {
        var rules = new[] { TestRules.Stateless("R1", "GreaterThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 50) }) };
        var input = new[]
        {
            Readings.Temp("b", "2025-06-01T08:00:10Z", 3, 60),
            Readings.Temp("a", "2025-06-01T08:00:10Z", 2, 40),
            Readings.Temp("a", "2025-06-01T08:00:00Z", 1, 60),
        };

        var result = _evaluator.Evaluate(Readings.Shuffled(input), rules);

        result.Classifications.Select(c => c.Key.Seq).ShouldBe(new long[] { 1, 2, 3 });
        result.Classifications.Select(c => c.Classification).ShouldBe(
            new[] { Classification.Unacceptable, Classification.Acceptable, Classification.Unacceptable });
    }

    [Fact]
    public void The_result_does_not_depend_on_input_order()
    {
        var rules = new[]
        {
            TestRules.Stateless("R1", "GreaterThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 70) }),
            TestRules.Stateless("R2", "Between", Metric.Pressure, parameters: new[] { ("min", 1.0), ("max", 8.0) }),
        };
        var input = Readings.Series("d", Metric.Temperature, "2025-06-01T08:00:00Z", 10, 69, 71, 72, 70, 90)
            .Concat(Readings.Series("d", Metric.Pressure, "2025-06-01T08:00:00Z", 10, 0.5, 5, 9, 4));

        var sorted = _evaluator.Evaluate(input, rules);
        var shuffled = _evaluator.Evaluate(Readings.Shuffled(input), rules);

        shuffled.Classifications.Select(c => (c.Key, c.Classification, string.Join("|", c.Violations.Select(v => v.Reason))))
            .ShouldBe(sorted.Classifications.Select(c => (c.Key, c.Classification, string.Join("|", c.Violations.Select(v => v.Reason)))));
        shuffled.RuleEvaluations.ShouldBe(sorted.RuleEvaluations);
    }
}
