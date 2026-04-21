using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Reads task data and performs low-coupling task mutations that have no
/// cross-boundary side effects (no activity events, no room messages, no
/// agent location changes).
///
/// For state transitions with side effects, see <see cref="ITaskLifecycleService"/>.
/// For cross-boundary operations (room + agent + task), see <see cref="ITaskOrchestrationService"/>.
/// </summary>
public interface ITaskQueryService
{
    // ── Queries ─────────────────────────────────────────────────

    /// <summary>
    /// Returns all tasks, optionally filtered by sprint. Scoped to the active workspace.
    /// Tasks are ordered by priority (ascending) then created date (descending).
    /// Includes dependency metadata on each snapshot.
    /// </summary>
    Task<List<TaskSnapshot>> GetTasksAsync(string? sprintId = null);

    /// <summary>
    /// Returns a single task by ID, or null if not found.
    /// Includes comment count and dependency metadata.
    /// </summary>
    Task<TaskSnapshot?> GetTaskAsync(string taskId);

    /// <summary>
    /// Finds the first non-cancelled task matching the given title (case-insensitive).
    /// Returns null if no match.
    /// </summary>
    Task<TaskSnapshot?> FindTaskByTitleAsync(string title);

    /// <summary>
    /// Returns tasks in <c>InReview</c> or <c>AwaitingValidation</c> status,
    /// scoped to the active workspace.
    /// </summary>
    Task<List<TaskSnapshot>> GetReviewQueueAsync();

    /// <summary>
    /// Returns task IDs and PR numbers for tasks with active pull requests
    /// (status is Open, ReviewRequested, or ChangesRequested).
    /// </summary>
    Task<List<(string TaskId, int PrNumber)>> GetTasksWithActivePrsAsync();

    /// <summary>
    /// Returns all comments for a task, ordered by creation date.
    /// </summary>
    Task<List<TaskComment>> GetTaskCommentsAsync(string taskId);

    /// <summary>
    /// Returns the number of comments on a task.
    /// </summary>
    Task<int> GetTaskCommentCountAsync(string taskId);

    /// <summary>
    /// Returns evidence records for a task, optionally filtered by phase.
    /// </summary>
    Task<List<TaskEvidence>> GetTaskEvidenceAsync(string taskId, EvidencePhase? phase = null);

    /// <summary>
    /// Returns all spec-task links for a given task.
    /// </summary>
    Task<List<SpecTaskLink>> GetSpecLinksForTaskAsync(string taskId);

    /// <summary>
    /// Returns all tasks linked to a given spec section.
    /// </summary>
    Task<List<SpecTaskLink>> GetTasksForSpecAsync(string specSectionId);

    /// <summary>
    /// Returns completed or approved tasks that have no spec links.
    /// Used for spec-gap detection.
    /// </summary>
    Task<List<TaskSnapshot>> GetUnlinkedTasksAsync();

    // ── Low-Coupling Mutations ──────────────────────────────────
    // These update task entity fields without emitting activity events,
    // posting room messages, or changing agent locations.

    /// <summary>
    /// Assigns an agent to a task. Sets <c>AssignedAgentId</c> and
    /// <c>AssignedAgentName</c> on the task entity.
    /// </summary>
    /// <exception cref="InvalidOperationException">Task not found.</exception>
    Task<TaskSnapshot> AssignTaskAsync(string taskId, string agentId, string agentName);

    /// <summary>
    /// Updates a task's status. Automatically sets <c>StartedAt</c> when
    /// transitioning to Active, and <c>CompletedAt</c> when transitioning
    /// to Completed or Cancelled. Validates that dependency-blocked tasks
    /// cannot be activated.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Task not found, or task has unmet dependencies and target status is Active.
    /// </exception>
    Task<TaskSnapshot> UpdateTaskStatusAsync(string taskId, Shared.Models.TaskStatus status);

    /// <summary>
    /// Atomically transitions a task from <see cref="Shared.Models.TaskStatus.Approved"/>
    /// to <see cref="Shared.Models.TaskStatus.Merging"/>. Returns <c>true</c> if this
    /// caller won the claim, <c>false</c> if the task was not in Approved status
    /// (already merging, already completed, or never approved). Used to serialize
    /// concurrent <c>MERGE_TASK</c> handlers so a task is squash-merged at most once.
    /// </summary>
    Task<bool> TryClaimForMergeAsync(string taskId);

    /// <summary>
    /// Updates a task's priority level.
    /// </summary>
    /// <exception cref="InvalidOperationException">Task not found.</exception>
    Task<TaskSnapshot> UpdateTaskPriorityAsync(string taskId, TaskPriority priority);

    /// <summary>
    /// Records a branch name on a task. Branch metadata is write-once per task;
    /// if the task already has a different branch name, the operation is logged
    /// as a conflict and the existing value is preserved.
    /// </summary>
    /// <exception cref="InvalidOperationException">Task not found.</exception>
    Task<TaskSnapshot> UpdateTaskBranchAsync(string taskId, string branchName);

    /// <summary>
    /// Records pull request information on a task.
    /// </summary>
    /// <exception cref="InvalidOperationException">Task not found.</exception>
    Task<TaskSnapshot> UpdateTaskPrAsync(string taskId, string url, int number, PullRequestStatus status);

    /// <summary>
    /// Removes a spec-task link. No-op if the link does not exist.
    /// </summary>
    Task UnlinkTaskFromSpecAsync(string taskId, string specSectionId);

    // ── Bulk Operations ─────────────────────────────────────────
    // Bulk operations are best-effort: they skip individual items that fail
    // validation and return per-item results in BulkOperationResult.

    /// <summary>
    /// Updates the status of multiple tasks. Skips tasks that are not found
    /// or that fail dependency validation. Only allows safe statuses
    /// (Queued, Active, Blocked, AwaitingValidation, InReview).
    /// </summary>
    Task<BulkOperationResult> BulkUpdateStatusAsync(IReadOnlyList<string> taskIds, Shared.Models.TaskStatus status);

    /// <summary>
    /// Assigns multiple tasks to a single agent. Skips tasks that are not found.
    /// </summary>
    Task<BulkOperationResult> BulkAssignAsync(IReadOnlyList<string> taskIds, string agentId, string? agentName);
}
