namespace AgentAcademy.Shared.Models;

/// <summary>
/// Snapshot of a task's current state, including phase tracking and
/// workstream progress for validation and implementation.
/// </summary>
public record TaskSnapshot(
    string Id,
    string Title,
    string Description,
    string SuccessCriteria,
    TaskStatus Status,
    CollaborationPhase CurrentPhase,
    string CurrentPlan,
    WorkstreamStatus ValidationStatus,
    string ValidationSummary,
    WorkstreamStatus ImplementationStatus,
    string ImplementationSummary,
    List<string> PreferredRoles,
    DateTime CreatedAt,
    DateTime UpdatedAt
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
    string? CorrelationId = null
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
