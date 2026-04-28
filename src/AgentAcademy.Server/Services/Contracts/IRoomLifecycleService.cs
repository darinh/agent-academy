namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Reason a non-archived room was skipped during a cleanup scan. Surfaced
/// to operators so a "0 archived" result no longer looks like a silent
/// failure — they can see whether each candidate was held back because it
/// is the main room, has no tasks at all, or has tasks still in flight.
/// </summary>
public enum RoomCleanupSkipReason
{
    /// <summary>Room is the persistent main collaboration room and is exempt from archiving.</summary>
    MainRoom,
    /// <summary>Room has no tasks recorded; cleanup only archives rooms whose tasks are all terminal, so a room with zero tasks is held back rather than silently archived.</summary>
    NoTasks,
    /// <summary>Room has at least one task that is not in a terminal status (Completed/Cancelled).</summary>
    ActiveTasks,
}

/// <summary>
/// Per-room reason a candidate was held back from archiving during
/// <see cref="IRoomLifecycleService.CleanupStaleRoomsDetailedAsync"/>.
/// </summary>
public sealed record RoomCleanupSkip(string RoomId, string RoomName, RoomCleanupSkipReason Reason)
{
    /// <summary>
    /// Stable, snake_case wire value for <see cref="Reason"/> exposed on the
    /// HTTP cleanup response and the CLEANUP_ROOMS command result. Decoupled
    /// from enum member names so renaming the enum cannot silently change the
    /// public API. New enum members MUST extend this switch and add a
    /// matching contract test in <c>RoomLifecycleServiceTests</c>.
    /// </summary>
    public string ReasonWireValue => Reason switch
    {
        RoomCleanupSkipReason.MainRoom => "main_room",
        RoomCleanupSkipReason.NoTasks => "no_tasks",
        RoomCleanupSkipReason.ActiveTasks => "active_tasks",
        _ => throw new InvalidOperationException(
            $"Unmapped {nameof(RoomCleanupSkipReason)} value '{Reason}' — add a wire mapping and contract test."),
    };
}

/// <summary>
/// Detailed result of a stale-room cleanup pass. <see cref="ArchivedCount"/>
/// matches the legacy <see cref="IRoomLifecycleService.CleanupStaleRoomsAsync"/>
/// integer; <see cref="SkippedCount"/> + <see cref="Skips"/> let operators
/// see why the cleanup didn't archive more (or anything).
/// </summary>
public sealed record RoomCleanupResult(
    int ArchivedCount,
    int SkippedCount,
    IReadOnlyList<RoomCleanupSkip> Skips);

/// <summary>
/// Handles room lifecycle operations: closing, reopening, auto-archiving,
/// and stale-room cleanup.
/// </summary>
public interface IRoomLifecycleService
{
    /// <summary>
    /// Returns true when the given room is the active workspace's main collaboration room
    /// or the legacy catalog default room.
    /// </summary>
    Task<bool> IsMainCollaborationRoomAsync(string roomId);

    /// <summary>
    /// Returns the set of room IDs that represent the persistent main collaboration
    /// room for <paramref name="workspacePath"/> — at most one workspace-resolved
    /// main room ID plus the legacy catalog default room ID. Used by terminal-status
    /// guards to keep the main room writable across sprint boundaries (B1).
    /// </summary>
    Task<HashSet<string>> GetExemptMainRoomIdsAsync(string workspacePath);

    /// <summary>
    /// Archives a non-main collaboration room. Already archived rooms are a no-op.
    /// </summary>
    Task CloseRoomAsync(string roomId);

    /// <summary>
    /// Reopens an archived room, restoring it to Idle status.
    /// </summary>
    Task ReopenRoomAsync(string roomId);

    /// <summary>
    /// Auto-archives a room when all its tasks have reached a terminal state.
    /// Skips main collaboration rooms.
    /// </summary>
    Task TryAutoArchiveRoomAsync(string roomId);

    /// <summary>
    /// Scans for stale rooms (all tasks terminal) that are still Active/Completed
    /// and archives them. Returns the count of rooms cleaned up.
    /// Equivalent to <c>(await <see cref="CleanupStaleRoomsDetailedAsync"/>).ArchivedCount</c>;
    /// prefer the detailed variant for new callers so operators can see why
    /// non-archived rooms were skipped.
    /// </summary>
    Task<int> CleanupStaleRoomsAsync();

    /// <summary>
    /// Same scan as <see cref="CleanupStaleRoomsAsync"/> but returns the per-room
    /// skip reasons in addition to the archived count. Surfaced through
    /// <c>POST /api/rooms/cleanup</c> and the <c>CLEANUP_ROOMS</c> command so
    /// a result with <c>archivedCount=0</c> includes <c>perRoomSkipReasons[]</c>
    /// explaining why (main room, no tasks, active tasks).
    /// </summary>
    Task<RoomCleanupResult> CleanupStaleRoomsDetailedAsync();

    /// <summary>
    /// Marks every non-terminal room belonging to the given workspace as
    /// <see cref="Shared.Models.RoomStatus.Completed"/> in response to a sprint
    /// completing or being cancelled. Evacuates agents back to the workspace
    /// default room and archives every active breakout descended from the
    /// workspace's rooms (including breakouts whose parent room was already
    /// marked Completed by stage advancement). Already-Archived rooms are
    /// skipped. Idempotent: a second call with no remaining non-terminal
    /// rooms or active breakouts is a no-op.
    /// Returns the number of rooms transitioned (does not include breakouts).
    /// </summary>
    Task<int> MarkSprintRoomsCompletedAsync(string workspacePath, string? sprintId = null);
}
