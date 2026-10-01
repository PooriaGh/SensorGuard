using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SensorGuard.Domain.Rules;

namespace SensorGuard.Application.Ports;

/// <summary>Port for loading rule definitions (seed data). Domain validation happens in <c>RuleCatalog</c>.</summary>
public interface IRuleSource
{
    Task<IReadOnlyList<RuleDefinition>> LoadAsync(CancellationToken cancellationToken);
}
