using SensorGuard.Domain.Model;

namespace SensorGuard.Infrastructure.Sqlite;

/// <summary>The single place that defines how a classification is stored in <c>readings.classification</c>.</summary>
internal static class ClassificationCodes
{
    public const string Acceptable = "A";
    public const string Unacceptable = "U";

    public static string For(Classification classification) =>
        classification == Classification.Acceptable ? Acceptable : Unacceptable;
}
