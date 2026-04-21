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
}
