using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Rules;

public enum OperatorKind
{
    Stateless,
    Stateful,
}

/// <summary>A rule exactly as read from data, before validation. Metric and operator are still plain text.</summary>
public sealed record RuleDefinition(
    string Id,
    string Name,
    bool Enabled,
    string Metric,
    string? DeviceId,
    string Operator,
    System.Collections.Generic.IReadOnlyDictionary<string, double> Parameters);

/// <summary>
/// A validated rule. Every rule describes the VIOLATING condition, except Between, which describes the acceptable
/// inclusive range. Built only by <see cref="RuleCatalog"/>.
/// </summary>
public sealed record Rule(
    RuleId Id,
    string Name,
    bool Enabled,
    Metric Metric,
    DeviceId? Device,
    string OperatorName,
    RuleParameters Parameters,
    OperatorKind Kind)
{
    /// <summary>Enabled, same metric, and (no device filter or the same device).</summary>
    public bool AppliesTo(SeriesKey series) =>
        Enabled
        && Metric == series.Metric
        && (Device is null || Device.Value == series.Device);
}
