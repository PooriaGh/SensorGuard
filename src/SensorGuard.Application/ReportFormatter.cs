using System.Globalization;
using System.Linq;
using System.Text;
using SensorGuard.Domain.Reporting;

namespace SensorGuard.Application;

/// <summary>Plain-text rendering of the processing report with a stable line order.</summary>
public static class ReportFormatter
{
    public static string Format(ProcessingReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("=== SensorGuard processing report ===");
        Line(text, "Total lines read", report.LinesRead);
        Line(text, "Blank lines skipped", report.BlankLines);
        Line(text, "Parsed readings", report.Parsed);
        Line(text, "Invalid records rejected", report.InvalidRejected);
        foreach (var (reason, count) in report.InvalidByReason.OrderBy(r => r.Key))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"    {reason,-18}{count}");
        }

        Line(text, "Duplicates removed (in file)", report.FileDuplicates);
        Line(text, "Already stored (earlier runs)", report.AlreadyStored);
        Line(text, "Newly stored readings", report.NewlyStored);
        Line(text, "Rules loaded (total)", report.RulesLoadedTotal);
        Line(text, "Rules loaded (enabled)", report.RulesLoadedEnabled);
        Line(text, "Rule evaluations performed", report.RuleEvaluations);
        Line(text, "Acceptable readings", report.Acceptable);
        Line(text, "Unacceptable readings", report.Unacceptable);
        Line(text, "Rule violations", report.RuleViolations);
        Line(text, "Alerts generated", report.AlertsGenerated);
        Line(text, "Alerts suppressed by cooldown", report.AlertsSuppressed);
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Elapsed",-34}{report.Elapsed.TotalMilliseconds:0} ms");
        return text.ToString();
    }

    private static void Line(StringBuilder text, string label, int value) =>
        text.AppendLine(CultureInfo.InvariantCulture, $"{label,-34}{value}");
}
