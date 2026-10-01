using System;
using System.Collections.Generic;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Domain.Reporting;
using Shouldly;
using Xunit;

namespace SensorGuard.Application.Tests;

public sealed class ReportFormatterTests
{
    private static ProcessingReport Sample() => new()
    {
        LinesRead = 2150,
        BlankLines = 3,
        Parsed = 2138,
        InvalidRejected = 9,
        InvalidByReason = new Dictionary<RejectionReason, int>
        {
            [RejectionReason.MalformedJson] = 1,
            [RejectionReason.MissingField] = 5,
            [RejectionReason.InvalidType] = 1,
            [RejectionReason.InvalidTimestamp] = 2,
        },
        FileDuplicates = 38,
        AlreadyStored = 100,
        NewlyStored = 2000,
        RulesLoadedTotal = 11,
        RulesLoadedEnabled = 10,
        RuleEvaluations = 3371,
        Acceptable = 1812,
        Unacceptable = 288,
        RuleViolations = 292,
        AlertsGenerated = 5,
        AlertsSuppressed = 4,
        Elapsed = TimeSpan.FromMilliseconds(245),
    };

    private static string Line(string text, string label)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith(label, StringComparison.Ordinal))
            {
                return line.TrimEnd('\r');
            }
        }

        throw new InvalidOperationException($"No line starting with '{label}' in:\n{text}");
    }

    [Theory]
    [InlineData("Total lines read", "2150")]
    [InlineData("Blank lines skipped", "3")]
    [InlineData("Parsed readings", "2138")]
    [InlineData("Invalid records rejected", "9")]
    [InlineData("Duplicates removed (in file)", "38")]
    [InlineData("Already stored (earlier runs)", "100")]
    [InlineData("Newly stored readings", "2000")]
    [InlineData("Rules loaded (total)", "11")]
    [InlineData("Rules loaded (enabled)", "10")]
    [InlineData("Rule evaluations performed", "3371")]
    [InlineData("Acceptable readings", "1812")]
    [InlineData("Unacceptable readings", "288")]
    [InlineData("Rule violations", "292")]
    [InlineData("Alerts generated", "5")]
    [InlineData("Alerts suppressed by cooldown", "4")]
    public void Every_required_count_is_printed_with_its_value(string label, string value)
    {
        Line(ReportFormatter.Format(Sample()), label).ShouldEndWith(value);
    }

    [Fact]
    public void The_invalid_count_is_broken_down_per_reason()
    {
        var text = ReportFormatter.Format(Sample());

        Line(text, "MalformedJson").ShouldEndWith("1");
        Line(text, "MissingField").ShouldEndWith("5");
        Line(text, "InvalidType").ShouldEndWith("1");
        Line(text, "InvalidTimestamp").ShouldEndWith("2");
    }

    [Fact]
    public void Elapsed_time_is_printed_in_milliseconds()
    {
        Line(ReportFormatter.Format(Sample()), "Elapsed").ShouldContain("245 ms");
    }

    [Fact]
    public void The_output_is_stable_for_equal_reports()
    {
        ReportFormatter.Format(Sample()).ShouldBe(ReportFormatter.Format(Sample()));
    }
}
