using System;
using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Model;

namespace SensorGuard.Domain.Rules;

public sealed class RuleValidationException : Exception
{
    public RuleValidationException(IReadOnlyList<string> errors)
        : base("Invalid rule definitions:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Validates rule definitions at startup and fails fast, naming every offending rule.</summary>
public sealed class RuleCatalog
{
    private readonly OperatorRegistry _registry;

    public RuleCatalog(OperatorRegistry registry) => _registry = registry;

    public IReadOnlyList<Rule> Build(IReadOnlyList<RuleDefinition> definitions)
    {
        var errors = new List<string>();
        var rules = new List<Rule>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            var label = Label(index, definition);
            var before = errors.Count;

            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                errors.Add($"{label}: id must not be empty");
            }
            else if (!seenIds.Add(definition.Id))
            {
                errors.Add($"{label}: duplicate rule id '{definition.Id}'");
            }

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                errors.Add($"{label}: name must not be empty");
            }

            if (!MetricNames.TryParse(definition.Metric, out var metric))
            {
                errors.Add($"{label}: unknown metric '{definition.Metric}' (expected temperature, pressure or vibration)");
            }

            if (definition.DeviceId is not null && string.IsNullOrWhiteSpace(definition.DeviceId))
            {
                errors.Add($"{label}: deviceId must not be empty when present");
            }

            var parameters = new RuleParameters(definition.Parameters);
            string? canonicalName = null;
            OperatorKind kind = default;
            var stateless = _registry.FindStateless(definition.Operator ?? string.Empty);
            var stateful = stateless is null ? _registry.FindStateful(definition.Operator ?? string.Empty) : null;
            IReadOnlyList<string> parameterErrors;
            if (stateless is not null)
            {
                canonicalName = stateless.Name;
                kind = OperatorKind.Stateless;
                parameterErrors = stateless.Validate(parameters);
            }
            else if (stateful is not null)
            {
                canonicalName = stateful.Name;
                kind = OperatorKind.Stateful;
                parameterErrors = stateful.Validate(parameters);
            }
            else
            {
                errors.Add($"{label}: unknown operator '{definition.Operator}'");
                parameterErrors = Array.Empty<string>();
            }

            errors.AddRange(parameterErrors.Select(e => $"{label}: {e}"));

            if (errors.Count == before)
            {
                rules.Add(new Rule(
                    new RuleId(definition.Id),
                    definition.Name,
                    definition.Enabled,
                    metric,
                    definition.DeviceId is null ? null : new DeviceId(definition.DeviceId),
                    canonicalName!,
                    parameters,
                    kind));
            }
        }

        return errors.Count > 0 ? throw new RuleValidationException(errors) : rules;
    }

    private static string Label(int index, RuleDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.Id)
            ? $"Rule #{index + 1}"
            : $"Rule '{definition.Id}' (\"{definition.Name}\")";
}
