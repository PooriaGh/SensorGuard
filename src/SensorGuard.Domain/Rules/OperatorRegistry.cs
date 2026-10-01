using System;
using System.Collections.Generic;

namespace SensorGuard.Domain.Rules;

/// <summary>
/// Resolves operators by name, case-insensitively. Built from whatever operators the composition root registers, so
/// adding an operator never touches this class or the evaluator (Open/Closed, Principle IV).
/// </summary>
public sealed class OperatorRegistry
{
    private readonly Dictionary<string, IStatelessOperator> _stateless = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IStatefulOperator> _stateful = new(StringComparer.OrdinalIgnoreCase);

    public OperatorRegistry(IEnumerable<IStatelessOperator> stateless, IEnumerable<IStatefulOperator> stateful)
    {
        foreach (var op in stateless)
        {
            Add(op.Name, () => _stateless.Add(op.Name, op));
        }

        foreach (var op in stateful)
        {
            Add(op.Name, () => _stateful.Add(op.Name, op));
        }
    }

    public IStatelessOperator? FindStateless(string name) => _stateless.GetValueOrDefault(name);

    public IStatefulOperator? FindStateful(string name) => _stateful.GetValueOrDefault(name);

    private void Add(string name, Action register)
    {
        if (_stateless.ContainsKey(name) || _stateful.ContainsKey(name))
        {
            throw new InvalidOperationException($"Operator '{name}' is registered more than once.");
        }

        register();
    }
}
