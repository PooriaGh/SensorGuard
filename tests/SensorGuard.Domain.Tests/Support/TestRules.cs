using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Model;
using SensorGuard.Domain.Rules;
using SensorGuard.Domain.Rules.Operators;

namespace SensorGuard.Domain.Tests.Support;

/// <summary>Builders for rules, operator registries and evaluators used by the domain tests.</summary>
internal static class TestRules
{
    public static RuleParameters Params(params (string Name, double Value)[] values) =>
        new(values.ToDictionary(v => v.Name, v => v.Value));

    public static Rule Stateless(
        string id,
        string op,
        Metric metric,
        string? device = null,
        bool enabled = true,
        params (string Name, double Value)[] parameters) =>
        new(
            new RuleId(id),
            id + " name",
            enabled,
            metric,
            device is null ? null : new DeviceId(device),
            op,
            Params(parameters),
            OperatorKind.Stateless);

    public static Rule Sustained(
        string id,
        Metric metric,
        double threshold,
        double durationSeconds,
        string? device = null,
        bool enabled = true) =>
        new(
            new RuleId(id),
            id + " name",
            enabled,
            metric,
            device is null ? null : new DeviceId(device),
            "SustainedAbove",
            Params(("threshold", threshold), ("durationSeconds", durationSeconds)),
            OperatorKind.Stateful);

    public static OperatorRegistry Registry(
        IEnumerable<IStatelessOperator>? extraStateless = null,
        IEnumerable<IStatefulOperator>? extraStateful = null) =>
        new(
            new IStatelessOperator[]
            {
                new GreaterThanOperator(),
                new GreaterThanOrEqualOperator(),
                new LessThanOperator(),
                new LessThanOrEqualOperator(),
                new EqualOperator(),
                new BetweenOperator(),
            }.Concat(extraStateless ?? Enumerable.Empty<IStatelessOperator>()),
            (extraStateful ?? Enumerable.Empty<IStatefulOperator>()));

    public static SeriesEvaluator Evaluator(OperatorRegistry? registry = null) =>
        new(registry ?? Registry());
}
