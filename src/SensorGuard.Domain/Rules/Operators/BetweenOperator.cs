using System.Collections.Generic;
using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Rules.Operators;

/// <summary>
/// Between describes the ACCEPTABLE inclusive range [min, max]; a reading below min or above max is a violation.
/// (Every other operator describes the violating condition itself.)
/// </summary>
public sealed class BetweenOperator : IStatelessOperator
{
    private const string MinParameter = "min";
    private const string MaxParameter = "max";

    public string Name => "Between";

    public IReadOnlyList<string> Validate(RuleParameters parameters)
    {
        var errors = new List<string>(parameters.RequireFinite(MinParameter, MaxParameter));
        if (errors.Count == 0 && parameters.Get(MinParameter) > parameters.Get(MaxParameter))
        {
            errors.Add("min must not be greater than max");
        }

        return errors;
    }

    public bool IsViolated(double value, Rule rule) =>
        value < rule.Parameters.Get(MinParameter) || value > rule.Parameters.Get(MaxParameter);

    public string Describe(double value, Rule rule) =>
        $"{MetricNames.Format(rule.Metric)} outside {Name} [{RuleParameters.Format(rule.Parameters.Get(MinParameter))}, {RuleParameters.Format(rule.Parameters.Get(MaxParameter))}] at {RuleParameters.Format(value)}";
}
