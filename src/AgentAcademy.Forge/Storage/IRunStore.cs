using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Storage;

/// <summary>
/// Persists forge run state to disk. Operations are atomic (write-temp-then-rename).
/// Not CRUD — designed around the state machine's actual write patterns.
/// </summary>
public interface IRunStore
{
    /// <summary>
    /// Initialize a new run directory with run.json, task.json, and frozen methodology.json.
    /// </summary>
    Task InitializeRunAsync(string runId, RunTrace run, TaskBrief task, MethodologyDefinition methodology, CancellationToken ct = default);

    /// <summary>
    /// Write a run.json snapshot (atomic replace).
    /// </summary>
    Task WriteRunSnapshotAsync(string runId, RunTrace run, CancellationToken ct = default);

    /// <summary>
    /// Read the current run.json.
    /// </summary>
    Task<RunTrace?> ReadRunAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Write per-phase scratch file (phases/NN-id/phase-run.json) for incremental progress.
    /// </summary>
    Task WritePhaseRunScratchAsync(string runId, int phaseIndex, string phaseId, PhaseRunTrace phaseRun, CancellationToken ct = default);

    /// <summary>
    /// Regenerate the top-level phase-runs.json rollup from per-phase scratch files.
    /// This is the authoritative trace artifact.
    /// </summary>
    Task WritePhaseRunsRollupAsync(string runId, IReadOnlyList<PhaseRunTrace> phaseRuns, CancellationToken ct = default);

    /// <summary>
    /// Read the top-level phase-runs.json rollup.
    /// </summary>
    Task<IReadOnlyList<PhaseRunTrace>?> ReadPhaseRunsRollupAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Read per-phase scratch file.
    /// </summary>
    Task<PhaseRunTrace?> ReadPhaseRunScratchAsync(string runId, int phaseIndex, string phaseId, CancellationToken ct = default);

    /// <summary>
    /// Write per-attempt files (prompt.txt, response.raw.txt, response.parsed.json, meta.json, validator-report.json).
    /// </summary>
    Task WriteAttemptFilesAsync(string runId, int phaseIndex, string phaseId, int attemptNumber, AttemptFiles files, CancellationToken ct = default);

    /// <summary>
    /// Write review-summary.json.
    /// </summary>
    Task WriteReviewSummaryAsync(string runId, ReviewSummaryTrace summary, CancellationToken ct = default);

    /// <summary>
    /// Append a structured NDJSON event to trace.log.
    /// </summary>
    Task AppendTraceEventAsync(string runId, object traceEvent, CancellationToken ct = default);

    /// <summary>
    /// List all run IDs in the store, sorted by creation time (ULID sort = time sort).
    /// </summary>
    Task<IReadOnlyList<string>> ListRunsAsync(CancellationToken ct = default);

    /// <summary>
    /// Check if a run directory exists.
    /// </summary>
    Task<bool> RunExistsAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Get the path to a run's directory.
    /// </summary>
    string GetRunDirectory(string runId);
}

/// <summary>
/// Bundle of per-attempt files. All fields are optional — write what you have.
/// </summary>
public sealed record AttemptFiles
{
    public string? PromptText { get; init; }
    public string? ResponseRaw { get; init; }
    public string? ResponseParsedJson { get; init; }
    public string? ValidatorReportJson { get; init; }
    public string? MetaJson { get; init; }
}
