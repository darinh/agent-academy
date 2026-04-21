using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Handles task state transitions that have side effects: activity events,
/// room messages, review workflow, comments, and spec linking.
///
/// This is the middle layer between raw data access (<see cref="ITaskQueryService"/>)
/// and cross-boundary orchestration (<see cref="ITaskOrchestrationService"/>).
///
/// Methods that "stage" changes (e.g., <see cref="StageNewTask"/>) do NOT call
/// SaveChangesAsync — the caller owns the unit of work. Methods that "complete"
/// changes (e.g., <see cref="CompleteTaskCoreAsync"/>) DO call SaveChangesAsync.
/// </summary>
public interface ITaskLifecycleService
{
    // ── Task State Transitions ──────────────────────────────────

    /// <summary>
    /// Claims a task for an agent. Prevents double-claiming by another agent.
    /// Auto-activates tasks in <c>Queued</c> status. Sets <c>StartedAt</c>
    /// if not already set.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Task not found, task has unmet dependencies, or task is already claimed
    /// by a different agent.
    /// </exception>
    Task<TaskSnapshot> ClaimTaskAsync(string taskId, string agentId, string agentName);

    /// <summary>
    /// Releases a task claim. Only the currently assigned agent can release.
    /// Clears assignment fields and publishes a <c>TaskReleased</c> activity event.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Task not found, or releasing agent is not the current assignee.
    /// </exception>
    Task<TaskSnapshot> ReleaseTaskAsync(string taskId, string agentId);

    /// <summary>
    /// Syncs pull request status on a task. Returns the updated snapshot,
    /// or null if the task was not found or the status didn't change.
    /// </summary>
    Task<TaskSnapshot?> SyncTaskPrStatusAsync(string taskId, PullRequestStatus newStatus);

    // ── Task Create / Complete / Reject ─────────────────────────

    /// <summary>
    /// Stages a new task entity, system messages, and activity events against
    /// the supplied room. Does NOT call SaveChangesAsync — the caller must
    /// commit the unit of work.
    /// </summary>
    /// <returns>The staged task snapshot and its creation activity event.</returns>
    (TaskSnapshot Task, ActivityEvent Activity) StageNewTask(
        TaskAssignmentRequest request,
        string roomId,
        string? workspacePath,
        bool isNewRoom,
        string correlationId);

    /// <summary>
    /// Associates a staged task with the active sprint for its workspace,
    /// if one exists. Must be called after <see cref="StageNewTask"/> and
    /// before SaveChangesAsync.
    /// </summary>
    Task AssociateTaskWithActiveSprintAsync(string taskId, string? workspacePath);

    /// <summary>
    /// Marks a task as Completed. Updates status, timestamps, commit metadata,
    /// and publishes unblock events for downstream dependencies. Saves changes.
    /// </summary>
    /// <returns>The completed task snapshot and its associated room ID (if any).</returns>
    /// <exception cref="InvalidOperationException">Task not found.</exception>
    Task<(TaskSnapshot Snapshot, string? RoomId)> CompleteTaskCoreAsync(
        string taskId,
        int commitCount,
        List<string>? testsCreated = null,
        string? mergeCommitSha = null);

    // ── Review Workflow ─────────────────────────────────────────

    /// <summary>
    /// Approves a task after review. Records the reviewer, increments review
    /// rounds, and publishes a <c>TaskApproved</c> activity event.
    /// Optionally records review findings as a comment.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Task not found, or task is not in <c>InReview</c> or <c>AwaitingValidation</c> status.
    /// </exception>
    Task<TaskSnapshot> ApproveTaskAsync(string taskId, string reviewerAgentId, string? findings = null);

    /// <summary>
    /// Requests changes on a task after review. Records the reviewer, increments
    /// review rounds, and publishes a <c>TaskChangesRequested</c> activity event.
    /// Enforces a maximum of 5 review rounds.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Task not found, task is not in <c>InReview</c> or <c>AwaitingValidation</c> status,
    /// or maximum review rounds exceeded.
    /// </exception>
    Task<TaskSnapshot> RequestChangesAsync(string taskId, string reviewerAgentId, string findings);

    /// <summary>
    /// Rejects a task (from Approved or Completed state). Does NOT call
    /// SaveChangesAsync — the caller owns the unit of work. This allows
    /// <see cref="ITaskOrchestrationService"/> to coordinate room reopening
    /// in the same transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Task not found, or task is not in <c>Approved</c> or <c>Completed</c> status.
    /// </exception>
    Task<RejectTaskResult> RejectTaskCoreAsync(
        string taskId,
        string reviewerAgentId,
        string reason,
        string? revertCommitSha = null);

    // ── Comments ────────────────────────────────────────────────

    /// <summary>
    /// Adds a comment, finding, evidence note, or blocker to a task.
    /// Publishes a <c>TaskCommentAdded</c> activity event and posts
    /// the comment content to the task's room.
    /// </summary>
    /// <exception cref="InvalidOperationException">Task not found.</exception>
    Task<TaskComment> AddTaskCommentAsync(
        string taskId,
        string agentId,
        string agentName,
        TaskCommentType commentType,
        string content);

    // ── Spec Linking ────────────────────────────────────────────

    /// <summary>
    /// Links a task to a spec section. Idempotent: if the (taskId, specSectionId)
    /// pair already exists, the link type and note are updated.
    /// Publishes a <c>SpecTaskLinked</c> activity event on creation.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Task not found, or invalid link type.
    /// </exception>
    Task<SpecTaskLink> LinkTaskToSpecAsync(
        string taskId,
        string specSectionId,
        string agentId,
        string agentName,
        string linkType = "Implements",
        string? note = null);
}
