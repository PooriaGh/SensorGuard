using System;
using System.Collections.Generic;
using System.Linq;
using SensorGuard.Domain.Ingestion;

namespace SensorGuard.Domain.Reporting;

/// <summary>End-of-run counts with the reconciliation invariants (Principle IX).</summary>
public sealed record ProcessingReport
{
    public int LinesRead { get; init; }

    public int BlankLines { get; init; }

    public int Parsed { get; init; }

    public int InvalidRejected { get; init; }

    public IReadOnlyDictionary<RejectionReason, int> InvalidByReason { get; init; } =
        new Dictionary<RejectionReason, int>();

    public int FileDuplicates { get; init; }

    public int AlreadyStored { get; init; }

    public int NewlyStored { get; init; }

    public int RulesLoadedTotal { get; init; }

    public int RulesLoadedEnabled { get; init; }

    public int RuleEvaluations { get; init; }

    public int Acceptable { get; init; }

    public int Unacceptable { get; init; }

    public int RuleViolations { get; init; }

    public int AlertsGenerated { get; init; }

    public int AlertsSuppressed { get; init; }

    public TimeSpan Elapsed { get; init; }

    public bool LinesReconcile() => LinesRead == BlankLines + Parsed + InvalidRejected;

    public bool InvalidBreakdownReconciles() => InvalidRejected == InvalidByReason.Values.Sum();

    public bool ParsedReconciles() => Parsed == NewlyStored + AlreadyStored + FileDuplicates;

    /// <summary>Acceptable/unacceptable count this file's distinct readings (research R7).</summary>
    public bool ClassificationReconciles() => NewlyStored + AlreadyStored == Acceptable + Unacceptable;

    /// <summary>Human-readable descriptions of every invariant that does not hold; empty when consistent.</summary>
    public IReadOnlyList<string> BrokenInvariants()
    {
        var broken = new List<string>();
        if (!LinesReconcile())
        {
            broken.Add($"lines read {LinesRead} != blank {BlankLines} + parsed {Parsed} + invalid {InvalidRejected}");
        }

        if (!InvalidBreakdownReconciles())
        {
            broken.Add($"invalid {InvalidRejected} != sum of per-reason counts {InvalidByReason.Values.Sum()}");
        }

        if (!ParsedReconciles())
        {
            broken.Add($"parsed {Parsed} != newly stored {NewlyStored} + already stored {AlreadyStored} + file duplicates {FileDuplicates}");
        }

        if (!ClassificationReconciles())
        {
            broken.Add($"newly + already stored {NewlyStored + AlreadyStored} != classified {Acceptable + Unacceptable} (acceptable + unacceptable)");
        }

        return broken;
    }
}
