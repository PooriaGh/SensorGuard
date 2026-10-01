using SensorGuard.Domain.Model;
using SensorGuard.Domain.Tests.Support;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Rules;

public sealed class RuleApplicabilityTests
{
    private static SeriesKey Series(string device, Metric metric) => new(new DeviceId(device), metric);

    [Fact]
    public void A_rule_with_a_device_applies_only_to_that_device()
    {
        var rule = TestRules.Stateless("R", "GreaterThan", Metric.Temperature, device: "PUMP-01", parameters: new (string, double)[] { ("value", 1) });

        rule.AppliesTo(Series("PUMP-01", Metric.Temperature)).ShouldBeTrue();
        rule.AppliesTo(Series("PUMP-02", Metric.Temperature)).ShouldBeFalse();
    }

    [Fact]
    public void A_rule_without_a_device_applies_to_every_device_carrying_the_metric()
    {
        var rule = TestRules.Stateless("R", "GreaterThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 1) });

        rule.AppliesTo(Series("PUMP-01", Metric.Temperature)).ShouldBeTrue();
        rule.AppliesTo(Series("FAN-03", Metric.Temperature)).ShouldBeTrue();
    }

    [Fact]
    public void A_rule_never_applies_to_a_different_metric()
    {
        var rule = TestRules.Stateless("R", "GreaterThan", Metric.Temperature, parameters: new (string, double)[] { ("value", 1) });
        var deviceRule = TestRules.Stateless("R2", "GreaterThan", Metric.Temperature, device: "PUMP-01", parameters: new (string, double)[] { ("value", 1) });

        rule.AppliesTo(Series("PUMP-01", Metric.Pressure)).ShouldBeFalse();
        deviceRule.AppliesTo(Series("PUMP-01", Metric.Vibration)).ShouldBeFalse();
    }

    [Fact]
    public void A_disabled_rule_never_applies()
    {
        var rule = TestRules.Stateless("R", "GreaterThan", Metric.Temperature, enabled: false, parameters: new (string, double)[] { ("value", 1) });

        rule.AppliesTo(Series("PUMP-01", Metric.Temperature)).ShouldBeFalse();
    }

    [Fact]
    public void Device_matching_is_case_sensitive()
    {
        var rule = TestRules.Stateless("R", "GreaterThan", Metric.Temperature, device: "PUMP-01", parameters: new (string, double)[] { ("value", 1) });

        rule.AppliesTo(Series("pump-01", Metric.Temperature)).ShouldBeFalse();
    }
}
