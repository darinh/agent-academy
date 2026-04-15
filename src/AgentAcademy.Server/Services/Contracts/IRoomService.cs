using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Handles room operations: CRUD, queries, phase transitions, and room messages.
/// Lifecycle operations (close, reopen, archive, cleanup) are on <see cref="IRoomLifecycleService"/>.
/// Snapshot building is on <see cref="IRoomSnapshotBuilder"/>.
/// Workspace–room management is on <see cref="IWorkspaceRoomService"/>.
/// </summary>
public interface IRoomService
{
    // ── Room Queries ────────────────────────────────────────────

    /// <summary>
    /// Returns rooms for the active workspace as snapshots, ordered by name.
    /// Rooms without a workspace are included only when no workspace is active.
    /// </summary>
    Task<List<RoomSnapshot>> GetRoomsAsync(bool includeArchived = false);

    /// <summary>
    /// Returns a single room snapshot by ID, or null if not found.
    /// </summary>
    Task<RoomSnapshot?> GetRoomAsync(string roomId);

    /// <summary>
    /// Returns messages in a room with cursor-based pagination.
    /// Only returns non-DM messages from the active conversation session.
    /// </summary>
    Task<(List<ChatEnvelope> Messages, bool HasMore)> GetRoomMessagesAsync(
        string roomId, string? afterMessageId = null, int limit = 50, string? sessionId = null);

    /// <summary>
    /// Resolves the project name for a room by following
    /// roomId → RoomEntity.WorkspacePath → WorkspaceEntity.ProjectName.
    /// Falls back to the workspace directory basename when ProjectName is null.
    /// </summary>
    Task<string?> GetProjectNameForRoomAsync(string roomId);

    /// <summary>
    /// Returns the humanized project name of the active workspace.
    /// Falls back to directory basename when ProjectName is null.
    /// </summary>
    Task<string?> GetActiveProjectNameAsync();

    /// <summary>
    /// Returns IDs of rooms whose most recent message was sent by a human user
    /// and has not been followed by an agent or system response.
    /// </summary>
    Task<List<string>> GetRoomsWithPendingHumanMessagesAsync();

    // ── Room CRUD ───────────────────────────────────────────────

    /// <summary>
    /// Creates a new room with the given name and optional description.
    /// </summary>
    Task<RoomSnapshot> CreateRoomAsync(string name, string? description = null);

    /// <summary>
    /// Renames a room and publishes a RoomRenamed activity event.
    /// Returns the updated snapshot, or null if the room doesn't exist.
    /// </summary>
    Task<RoomSnapshot?> RenameRoomAsync(string roomId, string newName);

    /// <summary>
    /// Sets or clears the topic of a room.
    /// </summary>
    Task<RoomSnapshot> SetRoomTopicAsync(string roomId, string? topic);

    // ── Phase Transitions ───────────────────────────────────────

    /// <summary>
    /// Transitions a room (and its active task) to a new phase.
    /// </summary>
    Task<RoomSnapshot> TransitionPhaseAsync(
        string roomId, CollaborationPhase targetPhase, string? reason = null, bool force = false);

    /// <summary>
    /// Returns the path of the currently active workspace, or null if none.
    /// </summary>
    Task<string?> GetActiveWorkspacePathAsync();
}
