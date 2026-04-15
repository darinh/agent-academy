using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Records structured verification evidence against tasks and evaluates
/// whether a task meets minimum evidence requirements for phase transitions
/// (evidence gates).
///
/// Evidence records are append-only and immutable. Each record captures a
/// single check (e.g., "dotnet test passed") with its tool, command, result,
/// and output snippet.
/// </summary>
public interface ITaskEvidenceService
{
    /// <summary>
    /// Records a structured verification check against a task.
    /// Publishes a <c>QualityGateChecked</c> activity event.
    /// Output snippets are truncated to 500 characters.
    /// </summary>
    /// <param name="taskId">The task to record evidence for.</param>
    /// <param name="agentId">The agent recording the evidence.</param>
    /// <param name="agentName">Display name of the recording agent.</param>
    /// <param name="phase">Evidence phase: Baseline, After, or Review.</param>
    /// <param name="checkName">Human-readable check name (e.g., "Unit tests").</param>
    /// <param name="tool">Tool used (e.g., "dotnet test", "npm run build").</param>
    /// <param name="command">Exact command executed (optional).</param>
    /// <param name="exitCode">Process exit code (optional).</param>
    /// <param name="outputSnippet">Relevant output excerpt (truncated to 500 chars).</param>
    /// <param name="passed">Whether the check passed.</param>
    /// <exception cref="InvalidOperationException">
    /// Task not found, or invalid evidence phase.
    /// </exception>
    Task<TaskEvidence> RecordEvidenceAsync(
        string taskId,
        string agentId,
        string agentName,
        EvidencePhase phase,
        string checkName,
        string tool,
        string? command,
        int? exitCode,
        string? outputSnippet,
        bool passed);

    /// <summary>
    /// Checks whether a task meets the minimum evidence requirements for
    /// its next phase transition.
    ///
    /// Gate requirements:
    /// <list type="bullet">
    ///   <item>Active → AwaitingValidation: ≥1 passing "After" check</item>
    ///   <item>AwaitingValidation → InReview: ≥2 passing "After" checks</item>
    ///   <item>InReview → Approved: ≥1 passing "Review" check</item>
    /// </list>
    /// </summary>
    /// <exception cref="InvalidOperationException">Task not found.</exception>
    Task<GateCheckResult> CheckGatesAsync(string taskId);
}
