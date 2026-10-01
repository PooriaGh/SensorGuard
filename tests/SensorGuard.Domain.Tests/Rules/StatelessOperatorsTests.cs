using SensorGuard.Domain.Model;
using SensorGuard.Domain.Rules;
using SensorGuard.Domain.Rules.Operators;
using SensorGuard.Domain.Tests.Support;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Rules;

/// <summary>Every rule describes the VIOLATING condition, except Between which describes the acceptable range.</summary>
public sealed class StatelessOperatorsTests
{
    private static Rule R(string op, Metric metric, params (string, double)[] p) =>
        TestRules.Stateless("R", op, metric, parameters: p);

    [Theory]
    [InlineData(100.1, true)]
    [InlineData(100.0, false)]
    [InlineData(99.9, false)]
    public void GreaterThan_fires_strictly_above_the_value(double reading, bool fires)
    {
        var op = new GreaterThanOperator();

        op.Name.ShouldBe("GreaterThan");
        op.IsViolated(reading, R("GreaterThan", Metric.Temperature, ("value", 100))).ShouldBe(fires);
    }

    [Theory]
    [InlineData(100.1, true)]
    [InlineData(100.0, true)]
    [InlineData(99.9, false)]
    public void GreaterThanOrEqual_fires_at_and_above_the_value(double reading, bool fires)
    {
        new GreaterThanOrEqualOperator()
            .IsViolated(reading, R("GreaterThanOrEqual", Metric.Temperature, ("value", 100)))
            .ShouldBe(fires);
    }

    [Theory]
    [InlineData(-0.001, true)]
    [InlineData(0.0, false)]
    [InlineData(0.5, false)]
    public void LessThan_fires_strictly_below_the_value(double reading, bool fires)
    {
        new LessThanOperator()
            .IsViolated(reading, R("LessThan", Metric.Vibration, ("value", 0)))
            .ShouldBe(fires);
    }

    [Theory]
    [InlineData(-0.001, true)]
    [InlineData(0.0, true)]
    [InlineData(0.5, false)]
    public void LessThanOrEqual_fires_at_and_below_the_value(double reading, bool fires)
    {
        new LessThanOrEqualOperator()
            .IsViolated(reading, R("LessThanOrEqual", Metric.Vibration, ("value", 0)))
            .ShouldBe(fires);
    }

    [Fact]
    public void Equal_fires_on_exact_numeric_equality_with_no_tolerance()
    {
        var op = new EqualOperator();
        var rule = R("Equal", Metric.Vibration, ("value", -9999));

        op.IsViolated(-9999, rule).ShouldBeTrue();
        op.IsViolated(-9999.0000001, rule).ShouldBeFalse();
        op.IsViolated(0.1 + 0.2, R("Equal", Metric.Vibration, ("value", 0.3))).ShouldBeFalse();
    }

    [Theory]
    [InlineData(-0.1, true)]
    [InlineData(0.0, false)]
    [InlineData(6.0, false)]
    [InlineData(12.0, false)]
    [InlineData(12.1, true)]
    public void Between_describes_the_acceptable_range_inclusive_and_fires_outside_it(double reading, bool fires)
    {
        var rule = R("Between", Metric.Pressure, ("min", 0), ("max", 12));

        new BetweenOperator().IsViolated(reading, rule).ShouldBe(fires);
    }

    [Fact]
    public void Describe_produces_a_human_readable_reason()
    {
        new GreaterThanOperator()
            .Describe(103.2, R("GreaterThan", Metric.Temperature, ("value", 100)))
            .ShouldBe("temperature GreaterThan 100 fires at 103.2");

        new LessThanOperator()
            .Describe(-0.629, R("LessThan", Metric.Vibration, ("value", 0)))
            .ShouldBe("vibration LessThan 0 fires at -0.629");

        new BetweenOperator()
            .Describe(14.22, R("Between", Metric.Pressure, ("min", 0), ("max", 12)))
            .ShouldBe("pressure outside Between [0, 12] at 14.22");
    }

    [Theory]
    [InlineData("GreaterThan")]
    [InlineData("GreaterThanOrEqual")]
    [InlineData("LessThan")]
    [InlineData("LessThanOrEqual")]
    [InlineData("Equal")]
    public void Single_value_operators_require_a_value_parameter(string name)
    {
        var op = TestRules.Registry().FindStateless(name)!;

        op.Validate(TestRules.Params()).ShouldNotBeEmpty();
        op.Validate(TestRules.Params(("value", 1))).ShouldBeEmpty();
    }

    [Fact]
    public void Between_requires_min_and_max_with_min_not_above_max()
    {
        var op = new BetweenOperator();

        op.Validate(TestRules.Params(("min", 1))).ShouldNotBeEmpty();
        op.Validate(TestRules.Params(("max", 1))).ShouldNotBeEmpty();
        op.Validate(TestRules.Params(("min", 5), ("max", 1))).ShouldNotBeEmpty();
        op.Validate(TestRules.Params(("min", 1), ("max", 1))).ShouldBeEmpty();
        op.Validate(TestRules.Params(("min", 0), ("max", 12))).ShouldBeEmpty();
    }

    [Fact]
    public void Parameters_must_be_finite()
    {
        new GreaterThanOperator().Validate(TestRules.Params(("value", double.NaN))).ShouldNotBeEmpty();
        new BetweenOperator().Validate(TestRules.Params(("min", 0), ("max", double.PositiveInfinity))).ShouldNotBeEmpty();
    }
}
