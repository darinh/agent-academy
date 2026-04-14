using System.ComponentModel.DataAnnotations;

namespace AgentAcademy.Shared.Models;

/// <summary>
/// Snapshot of a task's current state, including phase tracking,
/// workstream progress, agent assignment, and git/PR metadata.
/// </summary>
public record TaskSnapshot(
    string Id,
    string Title,
    string Description,
    string SuccessCriteria,
    TaskStatus Status,
    TaskType Type,
    CollaborationPhase CurrentPhase,
    string CurrentPlan,
    WorkstreamStatus ValidationStatus,
    string ValidationSummary,
    WorkstreamStatus ImplementationStatus,
    string ImplementationSummary,
    List<string> PreferredRoles,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    TaskSize? Size = null,
    DateTime? StartedAt = null,
    DateTime? CompletedAt = null,
    string? AssignedAgentId = null,
    string? AssignedAgentName = null,
    bool UsedFleet = false,
    List<string>? FleetModels = null,
    string? BranchName = null,
    string? PullRequestUrl = null,
    int? PullRequestNumber = null,
    PullRequestStatus? PullRequestStatus = null,
    string? ReviewerAgentId = null,
    int ReviewRounds = 0,
    List<string>? TestsCreated = null,
    int CommitCount = 0,
    string? MergeCommitSha = null,
    int CommentCount = 0,
    string? WorkspacePath = null,
    string? SprintId = null,
    List<string>? DependsOnTaskIds = null,
    List<string>? BlockingTaskIds = null
);

/// <summary>
/// An individual work item assigned to an agent within a breakout room.
/// Tracks progress, evidence of completion, and reviewer feedback.
/// </summary>
public record TaskItem(
    string Id,
    string Title,
    string Description,
    TaskItemStatus Status,
    string AssignedTo,
    string RoomId,
    string? BreakoutRoomId,
    string? Evidence,
    string? Feedback,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

/// <summary>
/// Request to create and assign a new task to a room.
/// </summary>
public record TaskAssignmentRequest(
    [Required, StringLength(200)] string Title,
    [Required, MinLength(1), StringLength(10_000)] string Description,
    [Required, MinLength(1), StringLength(5_000)] string SuccessCriteria,
    [StringLength(100)] string? RoomId,
    List<string> PreferredRoles,
    [EnumDataType(typeof(TaskType))] TaskType Type = TaskType.Feature,
    string? CorrelationId = null,
    [StringLength(50_000)] string? CurrentPlan = null
);

/// <summary>
/// Result of a task assignment, containing the created room, task, and
/// corresponding activity event.
/// </summary>
public record TaskAssignmentResult(
    string CorrelationId,
    RoomSnapshot Room,
    TaskSnapshot Task,
    ActivityEvent Activity
);

/// <summary>
/// A comment or finding attached to a task.
/// </summary>
public record TaskComment(
    string Id,
    string TaskId,
    string AgentId,
    string AgentName,
    TaskCommentType CommentType,
    string Content,
    DateTime CreatedAt
);

/// <summary>
/// Links a task to a spec section for traceability.
/// </summary>
/// <summary>
/// A structured verification check recorded against a task.
/// </summary>
public record TaskEvidence(
    string Id,
    string TaskId,
    EvidencePhase Phase,
    string CheckName,
    string Tool,
    string? Command,
    int? ExitCode,
    string? OutputSnippet,
    bool Passed,
    string AgentId,
    string AgentName,
    DateTime CreatedAt
);

/// <summary>
/// Result of checking whether a task meets evidence gates for a phase transition.
/// </summary>
public record GateCheckResult(
    string TaskId,
    string CurrentPhase,
    string TargetPhase,
    bool Met,
    int RequiredChecks,
    int PassedChecks,
    List<string> MissingChecks,
    List<TaskEvidence> Evidence
);

public record SpecTaskLink(
    string Id,
    string TaskId,
    string SpecSectionId,
    SpecLinkType LinkType,
    string LinkedByAgentId,
    string LinkedByAgentName,
    string? Note,
    DateTime CreatedAt
);

/// <summary>
/// Detailed dependency information for a task, including the dependent task summaries.
/// </summary>
public record TaskDependencyInfo(
    string TaskId,
    List<TaskDependencySummary> DependsOn,
    List<TaskDependencySummary> DependedOnBy
);

/// <summary>
/// Lightweight summary of a dependency target task.
/// </summary>
public record TaskDependencySummary(
    string TaskId,
    string Title,
    TaskStatus Status,
    bool IsSatisfied
);

// ── Bulk Operation Models ───────────────────────────────────────────

/// <summary>
/// Request to update the status of multiple tasks at once.
/// Only safe statuses allowed: Queued, Active, Blocked, AwaitingValidation, InReview.
/// </summary>
public record BulkUpdateStatusRequest(
    [Required, MinLength(1)] List<string> TaskIds,
    [Required] TaskStatus Status);

/// <summary>
/// Request to assign multiple tasks to a single agent.
/// </summary>
public record BulkAssignRequest(
    [Required, MinLength(1)] List<string> TaskIds,
    [Required, StringLength(100, MinimumLength = 1)] string AgentId,
    [StringLength(200)] string? AgentName = null);

/// <summary>
/// Result of a bulk task operation. Contains successfully updated tasks and per-item errors.
/// </summary>
public record BulkOperationResult(
    int Requested,
    int Succeeded,
    int Failed,
    List<TaskSnapshot> Updated,
    List<BulkOperationError> Errors);

/// <summary>
/// Per-task error from a bulk operation.
/// </summary>
public record BulkOperationError(string TaskId, string Code, string Error);
