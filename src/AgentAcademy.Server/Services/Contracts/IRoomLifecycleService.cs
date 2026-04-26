namespace AgentAcademy.Server.Services.Contracts;

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
    /// </summary>
    Task<int> CleanupStaleRoomsAsync();

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
