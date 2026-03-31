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
    int CommentCount = 0
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
    string Title,
    string Description,
    string SuccessCriteria,
    string? RoomId,
    List<string> PreferredRoles,
    TaskType Type = TaskType.Feature,
    string? CorrelationId = null,
    string? CurrentPlan = null
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
