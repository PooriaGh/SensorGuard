using System.Collections.Generic;
using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Rules.Operators;

/// <summary>Shared shape of the comparison operators: one <c>value</c> parameter, fires when the comparison holds.</summary>
public abstract class SingleValueOperator : IStatelessOperator
{
    private const string ValueParameter = "value";

    public abstract string Name { get; }

    public IReadOnlyList<string> Validate(RuleParameters parameters) => parameters.RequireFinite(ValueParameter);

    public bool IsViolated(double value, Rule rule) => Fires(value, rule.Parameters.Get(ValueParameter));

    public string Describe(double value, Rule rule) =>
        $"{MetricNames.Format(rule.Metric)} {Name} {RuleParameters.Format(rule.Parameters.Get(ValueParameter))} fires at {RuleParameters.Format(value)}";

    /// <summary>True when <paramref name="reading"/> satisfies the rule's stated condition (a violation).</summary>
    protected abstract bool Fires(double reading, double limit);
}

public sealed class GreaterThanOperator : SingleValueOperator
{
    public override string Name => "GreaterThan";

    protected override bool Fires(double reading, double limit) => reading > limit;
}

public sealed class GreaterThanOrEqualOperator : SingleValueOperator
{
    public override string Name => "GreaterThanOrEqual";

    protected override bool Fires(double reading, double limit) => reading >= limit;
}

public sealed class LessThanOperator : SingleValueOperator
{
    public override string Name => "LessThan";

    protected override bool Fires(double reading, double limit) => reading < limit;
}

public sealed class LessThanOrEqualOperator : SingleValueOperator
{
    public override string Name => "LessThanOrEqual";

    protected override bool Fires(double reading, double limit) => reading <= limit;
}

/// <summary>Exact numeric equality, no tolerance.</summary>
public sealed class EqualOperator : SingleValueOperator
{
    public override string Name => "Equal";

    protected override bool Fires(double reading, double limit) => reading == limit;
}
