namespace AgentAcademy.Forge.Models;

/// <summary>
/// A controlled test case with intentionally injected drift for measuring
/// fidelity detection accuracy. Each defect pairs a source-intent artifact
/// with a drifted output artifact and declares the expected fidelity verdict.
/// </summary>
public sealed record SeededDefect
{
    /// <summary>Short ID, e.g. "SD-OMIT".</summary>
    public required string Id { get; init; }

    public required string Description { get; init; }

    /// <summary>Valid source_intent/v1 artifact.</summary>
    public required ArtifactEnvelope SourceIntent { get; init; }

    /// <summary>Output artifact with drift baked in (structurally valid for fidelity benchmarking only).</summary>
    public required ArtifactEnvelope DriftedOutput { get; init; }

    /// <summary>Expected overall_match: "PASS", "FAIL", or "PARTIAL".</summary>
    public required string ExpectedOverallMatch { get; init; }

    /// <summary>Expected drift codes. Empty for clean cases.</summary>
    public required IReadOnlyList<string> ExpectedDriftCodes { get; init; }

    /// <summary>"blocking", "advisory", "clean", or "diagnostic" (multi-drift, not threshold-bearing).</summary>
    public required string DriftCategory { get; init; }
}

/// <summary>
/// Per-case result comparing LLM fidelity verdict against ground truth.
/// </summary>
public sealed record SeededDefectResult
{
    public required string DefectId { get; init; }
    public required string ExpectedMatch { get; init; }
    public required string? ActualMatch { get; init; }
    public required IReadOnlyList<string> ExpectedDriftCodes { get; init; }
    public required IReadOnlyList<string> ActualDriftCodes { get; init; }

    /// <summary>True if overall_match matched expected.</summary>
    public required bool MatchCorrect { get; init; }

    /// <summary>True if all expected drift codes were detected (superset is OK).</summary>
    public required bool DriftCodesDetected { get; init; }

    public required PhaseRunStatus FidelityStatus { get; init; }

    /// <summary>True if the fidelity executor failed to produce a verdict (LLM error, parse failure, etc.).</summary>
    public required bool Inconclusive { get; init; }
}

/// <summary>
/// Aggregated report across all seeded defect cases.
/// </summary>
public sealed record SeededDefectReport
{
    public required IReadOnlyList<SeededDefectResult> Results { get; init; }

    /// <summary>Fraction of blocking defects where the LLM correctly detected blocking drift (excludes inconclusive).</summary>
    public required double BlockingDetectionRate { get; init; }

    /// <summary>Fraction of advisory defects where the LLM correctly detected advisory drift (excludes inconclusive).</summary>
    public required double AdvisoryDetectionRate { get; init; }

    /// <summary>Fraction of all threshold-bearing cases where overall_match was correct (excludes diagnostic + inconclusive).</summary>
    public required double OverallMatchAccuracy { get; init; }

    /// <summary>False positive rate: fraction of clean cases where drift was incorrectly reported.</summary>
    public required double FalsePositiveRate { get; init; }

    /// <summary>Number of cases that were inconclusive (LLM failure, parse error).</summary>
    public required int InconclusiveCount { get; init; }

    /// <summary>True if blocking detection rate ≥ 80%.</summary>
    public bool MeetsBlockingThreshold => BlockingDetectionRate >= 0.80;

    /// <summary>True if advisory detection rate ≥ 60%.</summary>
    public bool MeetsAdvisoryThreshold => AdvisoryDetectionRate >= 0.60;

    /// <summary>Per-drift-code recall: fraction of cases targeting this code where it was detected.</summary>
    public required IReadOnlyDictionary<string, double> PerCodeRecall { get; init; }
}
