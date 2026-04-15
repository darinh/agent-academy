using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Handles all breakout room operations: creation, closure, queries,
/// task linking, agent session history, and breakout reopening.
/// </summary>
public interface IBreakoutRoomService
{
    // ── Creation & Closure ──────────────────────────────────────

    /// <summary>
    /// Creates a breakout room and moves the assigned agent to "Working" state.
    /// </summary>
    Task<BreakoutRoom> CreateBreakoutRoomAsync(
        string parentRoomId, string agentId, string name);

    /// <summary>
    /// Closes a breakout room and moves the agent back to idle.
    /// </summary>
    Task CloseBreakoutRoomAsync(
        string breakoutId,
        BreakoutRoomCloseReason closeReason = BreakoutRoomCloseReason.Completed);

    /// <summary>
    /// Finds the most recent breakout room for a task and reopens it if archived.
    /// Moves the assigned agent back into the breakout to address rejection findings.
    /// </summary>
    Task TryReopenBreakoutForTaskAsync(string taskId, string reason, string reviewerName);

    // ── Queries ─────────────────────────────────────────────────

    /// <summary>
    /// Returns a single breakout room by its ID, or null if not found.
    /// </summary>
    Task<BreakoutRoom?> GetBreakoutRoomAsync(string breakoutId);

    /// <summary>
    /// Returns breakout rooms for a given parent room.
    /// </summary>
    Task<List<BreakoutRoom>> GetBreakoutRoomsAsync(string parentRoomId);

    /// <summary>
    /// Returns all active breakout rooms across all parent rooms.
    /// </summary>
    Task<List<BreakoutRoom>> GetAllBreakoutRoomsAsync();

    /// <summary>
    /// Returns all breakout rooms (active and archived) assigned to a specific agent,
    /// ordered by most recent first. Used for agent session history.
    /// </summary>
    Task<List<BreakoutRoom>> GetAgentSessionsAsync(string agentId);

    // ── Task Linking ────────────────────────────────────────────

    /// <summary>
    /// Links a breakout room to a TaskEntity for reliable lookup during completion.
    /// </summary>
    Task SetBreakoutTaskIdAsync(string breakoutRoomId, string taskId);

    /// <summary>
    /// Returns the TaskEntity ID linked to a breakout room, or null if none.
    /// </summary>
    Task<string?> GetBreakoutTaskIdAsync(string breakoutRoomId);

    /// <summary>
    /// Moves the task linked to a breakout room into InReview status.
    /// Returns the updated task, or null when the breakout has no linked task.
    /// </summary>
    Task<TaskSnapshot?> TransitionBreakoutTaskToInReviewAsync(string breakoutRoomId);

    /// <summary>
    /// Ensures a breakout room has a single, explicitly linked TaskEntity.
    /// Task identity is keyed only by the breakout room's persisted TaskId.
    /// </summary>
    Task<string> EnsureTaskForBreakoutAsync(
        string breakoutRoomId,
        string title,
        string description,
        string agentId,
        string roomId,
        string? currentPlan = null,
        string? branchName = null);
}
