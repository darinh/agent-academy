using AgentAcademy.Server.Data.Entities;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Manages sprint lifecycle: creation, completion, cancellation, and timeout queries.
/// Stage advancement logic lives in <see cref="SprintStageService"/>.
/// </summary>
public interface ISprintService
{
    // ── Create ───────────────────────────────────────────────────

    /// <summary>
    /// Creates the next sprint for a workspace. If a previous sprint exists and
    /// has overflow artifacts, they are linked via <see cref="SprintEntity.OverflowFromSprintId"/>.
    /// Throws if there is already an active sprint for this workspace.
    /// </summary>
    Task<SprintEntity> CreateSprintAsync(string workspacePath, string? trigger = null);

    // ── Query ────────────────────────────────────────────────────

    /// <summary>Returns the active sprint for a workspace, or null if none.</summary>
    Task<SprintEntity?> GetActiveSprintAsync(string workspacePath);

    /// <summary>Returns a sprint by ID, or null if not found.</summary>
    Task<SprintEntity?> GetSprintByIdAsync(string sprintId);

    /// <summary>Returns all sprints for a workspace, ordered by number descending.</summary>
    Task<(List<SprintEntity> Items, int TotalCount)> GetSprintsForWorkspaceAsync(
        string workspacePath, int limit = 20, int offset = 0);

    // ── Completion ───────────────────────────────────────────────

    /// <summary>
    /// Marks a sprint as completed. Must be in the FinalSynthesis stage
    /// (or force=true to skip the stage check).
    /// </summary>
    Task<SprintEntity> CompleteSprintAsync(string sprintId, bool force = false);

    /// <summary>Cancels an active sprint.</summary>
    Task<SprintEntity> CancelSprintAsync(string sprintId);

    // ── Timeout Queries ──────────────────────────────────────────

    /// <summary>
    /// Returns active sprints that have been in AwaitingSignOff longer than the specified timeout.
    /// </summary>
    Task<List<SprintEntity>> GetTimedOutSignOffSprintsAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Returns active sprints whose total duration exceeds the specified limit.</summary>
    Task<List<SprintEntity>> GetOverdueSprintsAsync(TimeSpan maxDuration, CancellationToken ct = default);

    /// <summary>Auto-cancels a sprint that has exceeded the maximum duration.</summary>
    Task<SprintEntity> TimeOutSprintAsync(string sprintId, CancellationToken ct = default);
}
