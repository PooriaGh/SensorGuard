using SensorGuard.Application;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Reporting;
using SensorGuard.Domain.Rules;
using SensorGuard.Infrastructure.Files;
using SensorGuard.Infrastructure.Sqlite;

namespace SensorGuard.Api;

/// <summary>Holds the report of the ingestion run that happened at startup (for diagnostics and tests).</summary>
public sealed class StartupResult
{
    public ProcessingReport? Report { get; set; }
}

/// <summary>
/// Startup sequence (research R6): load and validate rules (fail fast, before the database is touched), create the
/// schema, ingest the input file and print the report. Only after this completes does the API start serving.
/// </summary>
public static class Startup
{
    public static async Task<ProcessingReport?> RunIngestionAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var definitions = await services.GetRequiredService<IRuleSource>().LoadAsync(cancellationToken);
            var rules = services.GetRequiredService<RuleCatalog>().Build(definitions);

            await services.GetRequiredService<SqliteDatabase>().InitializeAsync(cancellationToken);
            return await services.GetRequiredService<ProcessReadingsFile>().ExecuteAsync(rules, cancellationToken);
        }
        catch (RuleSourceException ex)
        {
            logger.LogCritical("Cannot start: {Message}", ex.Message);
            return null;
        }
        catch (RuleValidationException ex)
        {
            logger.LogCritical("Cannot start: {Message}", ex.Message);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogCritical("Cannot start: input could not be read: {Message}", ex.Message);
            return null;
        }
    }
}
