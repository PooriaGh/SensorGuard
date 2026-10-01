using Microsoft.Extensions.Options;
using SensorGuard.Application;
using SensorGuard.Application.Ports;
using SensorGuard.Domain.Evaluation;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Domain.Rules;
using SensorGuard.Domain.Rules.Operators;
using SensorGuard.Infrastructure.Files;
using SensorGuard.Infrastructure.Sqlite;

namespace SensorGuard.Api;

/// <summary>Composition root wiring. Adding an operator is one class plus one registration line below.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddSensorGuard(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SensorGuardOptions>(configuration.GetSection(SensorGuardOptions.SectionName));
        services.AddSingleton(TimeProvider.System);

        // Operators: the only place that knows which operators exist (Open/Closed, Principle IV).
        services.AddSingleton<IStatelessOperator, GreaterThanOperator>();
        services.AddSingleton<IStatelessOperator, GreaterThanOrEqualOperator>();
        services.AddSingleton<IStatelessOperator, LessThanOperator>();
        services.AddSingleton<IStatelessOperator, LessThanOrEqualOperator>();
        services.AddSingleton<IStatelessOperator, EqualOperator>();
        services.AddSingleton<IStatelessOperator, BetweenOperator>();
        services.AddSingleton<IStatefulOperator, SustainedAboveOperator>();

        services.AddSingleton<OperatorRegistry>();
        services.AddSingleton<RuleCatalog>();
        services.AddSingleton<ReadingValidator>();
        services.AddSingleton<Deduplicator>();
        services.AddSingleton(sp => new AlertPolicy(sp.GetRequiredService<IOptions<SensorGuardOptions>>().Value.AlertCooldown));
        services.AddSingleton<SeriesEvaluator>();

        services.AddSingleton<IReadingSource>(sp =>
            new JsonlReadingSource(PathResolver.Resolve(sp.GetRequiredService<IOptions<SensorGuardOptions>>().Value.InputFilePath)));
        services.AddSingleton<IRuleSource>(sp =>
            new JsonRuleSource(PathResolver.Resolve(sp.GetRequiredService<IOptions<SensorGuardOptions>>().Value.RulesFilePath)));
        services.AddSingleton(sp =>
            SqliteDatabase.ForFile(sp.GetRequiredService<IOptions<SensorGuardOptions>>().Value.DatabasePath));
        services.AddSingleton<SqliteSession>();
        services.AddSingleton<IUnitOfWork, SqliteUnitOfWork>();
        services.AddSingleton<IReadingStore, SqliteReadingStore>();
        services.AddSingleton<IRuleResultStore, SqliteRuleResultStore>();
        services.AddSingleton<IAlertStore, SqliteAlertStore>();
        services.AddSingleton<IReadingQuery, SqliteReadingQuery>();
        services.AddSingleton<IReportSink, ConsoleReportSink>();
        services.AddSingleton<ProcessReadingsFile>();
        services.AddSingleton<GetAggregation>();
        services.AddSingleton<StartupResult>();
        return services;
    }
}
