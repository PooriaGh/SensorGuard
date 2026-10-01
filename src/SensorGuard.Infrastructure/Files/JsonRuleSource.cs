using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Rules;

namespace SensorGuard.Infrastructure.Files;

public sealed class RuleSourceException : Exception
{
    public RuleSourceException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Loads rules.json: an array of flat objects. Known text/boolean properties are id, name, enabled, metric, deviceId
/// and operator; every other property must be a number and becomes an operator parameter, so a new operator can
/// introduce parameters without any loader change.
/// </summary>
public sealed class JsonRuleSource : IRuleSource
{
    private readonly string _path;

    public JsonRuleSource(string path) => _path = path;

    public async Task<IReadOnlyList<RuleDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(_path, cancellationToken);
        }
        catch (IOException ex)
        {
            throw new RuleSourceException($"Cannot read rules file '{_path}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RuleSourceException($"Cannot read rules file '{_path}': {ex.Message}", ex);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new RuleSourceException($"Rules file '{_path}' must contain a JSON array of rule objects.");
            }

            var rules = new List<RuleDefinition>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                rules.Add(ReadRule(element, rules.Count));
            }

            return rules;
        }
        catch (JsonException ex)
        {
            throw new RuleSourceException($"Rules file '{_path}' is not valid JSON (expected an array of rule objects): {ex.Message}", ex);
        }
    }

    private RuleDefinition ReadRule(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new RuleSourceException($"Rules file '{_path}': entry #{index + 1} is not an object (the file must be an array of rule objects).");
        }

        string id = string.Empty, name = string.Empty, metric = string.Empty, op = string.Empty;
        string? deviceId = null;
        var enabled = true;
        var parameters = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "id":
                    id = Text(property, index, id);
                    break;
                case "name":
                    name = Text(property, index, id);
                    break;
                case "metric":
                    metric = Text(property, index, id);
                    break;
                case "operator":
                    op = Text(property, index, id);
                    break;
                case "deviceId":
                    deviceId = property.Value.ValueKind == JsonValueKind.Null ? null : Text(property, index, id);
                    break;
                case "enabled":
                    enabled = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new RuleSourceException($"{Describe(index, id)}: 'enabled' must be true or false."),
                    };
                    break;
                default:
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var number))
                    {
                        throw new RuleSourceException($"{Describe(index, id)}: parameter '{property.Name}' must be a number.");
                    }

                    parameters[property.Name] = number;
                    break;
            }
        }

        return new RuleDefinition(id, name, enabled, metric, deviceId, op, parameters);
    }

    private static string Text(JsonProperty property, int index, string id) =>
        property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString() ?? string.Empty
            : throw new RuleSourceException($"{Describe(index, id)}: '{property.Name}' must be a string.");

    private static string Describe(int index, string id) =>
        string.IsNullOrEmpty(id) ? $"Rule #{index + 1}" : $"Rule '{id}'";
}
