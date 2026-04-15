using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Coordinates task operations that cross room, agent, and task boundaries.
/// This is the top-level entry point for task creation, completion, and
/// rejection — operations that involve room lifecycle, agent location,
/// messaging, and breakout management in addition to task state changes.
///
/// Delegates task-state changes to <see cref="ITaskLifecycleService"/> and
/// data access to <see cref="ITaskQueryService"/>.
/// </summary>
public interface ITaskOrchestrationService
{
    /// <summary>
    /// Creates a new task, optionally in an existing room or a new room.
    /// Handles room creation/lookup, task entity staging, sprint association,
    /// agent auto-join, and snapshot building.
    /// </summary>
    /// <returns>
    /// A result containing the correlation ID, room snapshot, task snapshot,
    /// and the creation activity event.
    /// </returns>
    Task<TaskAssignmentResult> CreateTaskAsync(TaskAssignmentRequest request);

    /// <summary>
    /// Completes a task and auto-archives its room if all tasks in the room
    /// are in a terminal state (Completed or Cancelled).
    /// </summary>
    /// <exception cref="InvalidOperationException">Task not found.</exception>
    Task<TaskSnapshot> CompleteTaskAsync(
        string taskId,
        int commitCount,
        List<string>? testsCreated = null,
        string? mergeCommitSha = null);

    /// <summary>
    /// Rejects a task, reopening its room and breakout room so the assigned
    /// agent can address the rejection findings. Posts rejection details
    /// to the task's room.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Task not found, or task is not in a rejectable state.
    /// </exception>
    Task<TaskSnapshot> RejectTaskAsync(
        string taskId,
        string reviewerAgentId,
        string reason,
        string? revertCommitSha = null);

    /// <summary>
    /// Posts a system note to the room associated with a task.
    /// No-op if the task has no associated room.
    /// </summary>
    Task PostTaskNoteAsync(string taskId, string message);

    // ── Bulk Operations ─────────────────────────────────────────
    // These delegate to ITaskQueryService for the data mutation and add
    // activity event publishing on top.

    /// <summary>
    /// Updates the status of multiple tasks (max 50) and publishes activity
    /// events for each successful update.
    /// </summary>
    Task<BulkOperationResult> BulkUpdateStatusAsync(IReadOnlyList<string> taskIds, Shared.Models.TaskStatus status);

    /// <summary>
    /// Assigns multiple tasks (max 50) to a single agent and publishes
    /// activity events for each successful assignment.
    /// </summary>
    Task<BulkOperationResult> BulkAssignAsync(IReadOnlyList<string> taskIds, string agentId, string? agentName);
}
