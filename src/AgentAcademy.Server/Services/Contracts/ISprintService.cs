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

    // ── Blocked Signal (P1.4 narrow scope) ──────────────────────

    /// <summary>
    /// Marks an Active sprint as blocked, recording the reason. Sprint status
    /// remains "Active" — the BlockedAt timestamp signals that the team is
    /// paused awaiting human intervention. Emits <see cref="ActivityEventType.SprintBlocked"/>
    /// the first time a sprint enters the blocked state; updates without a
    /// re-emit if already blocked. Throws if the sprint is not Active.
    /// </summary>
    /// <param name="sprintId">Sprint id.</param>
    /// <param name="reason">Human-readable reason. Must be non-empty.</param>
    Task<SprintEntity> MarkSprintBlockedAsync(string sprintId, string reason);

    /// <summary>
    /// Clears the blocked flag on a sprint. Idempotent — if the sprint is not
    /// blocked, returns it unchanged without emitting an event. Throws if the
    /// sprint is not Active.
    /// </summary>
    Task<SprintEntity> UnblockSprintAsync(string sprintId);

    // ── Timeout Queries ──────────────────────────────────────────

    /// <summary>
    /// Returns active sprints that have been in AwaitingSignOff longer than the specified timeout.
    /// Excludes sprints flagged as Blocked — those are explicitly paused
    /// waiting on a human and must not have sign-off auto-rejected.
    /// </summary>
    Task<List<SprintEntity>> GetTimedOutSignOffSprintsAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Returns active sprints whose total duration exceeds the specified limit.
    /// Blocked sprints (BlockedAt != null) are excluded — they are explicitly
    /// waiting on a human and must not be auto-cancelled by the timeout sweep.
    /// </summary>
    Task<List<SprintEntity>> GetOverdueSprintsAsync(TimeSpan maxDuration, CancellationToken ct = default);

    /// <summary>Auto-cancels a sprint that has exceeded the maximum duration.</summary>
    Task<SprintEntity> TimeOutSprintAsync(string sprintId, CancellationToken ct = default);

    // ── Self-Drive Counters (P1.2) ──────────────────────────────

    /// <summary>
    /// Atomically increments <see cref="SprintEntity.RoundsThisSprint"/> and
    /// <see cref="SprintEntity.RoundsThisStage"/> by <paramref name="innerRoundsExecuted"/>,
    /// updates <see cref="SprintEntity.LastRoundCompletedAt"/>, and (when
    /// <paramref name="wasSelfDriveContinuation"/> is true) increments
    /// <see cref="SprintEntity.SelfDriveContinuations"/>.
    ///
    /// Uses <c>ExecuteUpdateAsync</c> for atomicity (mirrors the P1.4 block
    /// pattern) so concurrent trigger runs cannot race the counter math.
    /// No-op (returns 0) when <paramref name="innerRoundsExecuted"/> &lt;= 0
    /// or the sprint is not Active. Returns the number of rows updated
    /// (0 or 1) — useful for callers that want to verify the counter actually
    /// landed before invoking the decision service.
    /// </summary>
    Task<int> IncrementRoundCountersAsync(
        string sprintId,
        int innerRoundsExecuted,
        bool wasSelfDriveContinuation,
        DateTime completedAt,
        CancellationToken ct = default);
}
