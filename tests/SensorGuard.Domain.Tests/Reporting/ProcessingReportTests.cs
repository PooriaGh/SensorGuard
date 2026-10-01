using System;
using System.Collections.Generic;
using SensorGuard.Domain.Ingestion;
using SensorGuard.Domain.Reporting;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests.Reporting;

public sealed class ProcessingReportTests
{
    private static ProcessingReport Valid() => new()
    {
        LinesRead = 20,
        BlankLines = 2,
        Parsed = 15,
        InvalidRejected = 3,
        InvalidByReason = new Dictionary<RejectionReason, int>
        {
            [RejectionReason.MalformedJson] = 1,
            [RejectionReason.MissingField] = 2,
        },
        FileDuplicates = 4,
        AlreadyStored = 5,
        NewlyStored = 6,
        RulesLoadedTotal = 3,
        RulesLoadedEnabled = 2,
        RuleEvaluations = 30,
        Acceptable = 8,
        Unacceptable = 3,
        RuleViolations = 4,
        AlertsGenerated = 1,
        AlertsSuppressed = 1,
        Elapsed = TimeSpan.FromMilliseconds(5),
    };

    [Fact]
    public void A_consistent_report_has_no_broken_invariants()
    {
        var report = Valid();

        report.LinesReconcile().ShouldBeTrue();
        report.InvalidBreakdownReconciles().ShouldBeTrue();
        report.ParsedReconciles().ShouldBeTrue();
        report.ClassificationReconciles().ShouldBeTrue(); // newly 6 + already 5 = acceptable 8 + unacceptable 3
    }

    [Fact]
    public void Lines_that_do_not_add_up_are_reported()
    {
        var report = Valid() with { LinesRead = 21 };

        report.LinesReconcile().ShouldBeFalse();
        report.BrokenInvariants().ShouldContain(s => s.Contains("lines"));
    }

    [Fact]
    public void Breakdown_that_does_not_sum_to_invalid_total_is_reported()
    {
        var report = Valid() with { InvalidRejected = 4, LinesRead = 21 };

        report.InvalidBreakdownReconciles().ShouldBeFalse();
    }

    [Fact]
    public void Parsed_must_equal_newly_plus_already_plus_duplicates()
    {
        var report = Valid() with { FileDuplicates = 5 };

        report.ParsedReconciles().ShouldBeFalse();
    }

    [Fact]
    public void Classification_mismatch_is_reported()
    {
        var report = Valid() with { Unacceptable = 4 };

        report.ClassificationReconciles().ShouldBeFalse();
        report.BrokenInvariants().ShouldContain(s => s.Contains("classified"));
    }

    [Fact]
    public void BrokenInvariants_is_empty_for_a_consistent_report()
    {
        Valid().BrokenInvariants().ShouldBeEmpty();
    }
}
