namespace AgentAcademy.Shared.Models;

/// <summary>
/// Snapshot of a collaboration room's current state, including participants
/// and recent message history.
/// </summary>
public record RoomSnapshot(
    string Id,
    string Name,
    RoomStatus Status,
    CollaborationPhase CurrentPhase,
    TaskSnapshot? ActiveTask,
    List<AgentPresence> Participants,
    List<ChatEnvelope> RecentMessages,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

/// <summary>
/// A breakout room is a child room assigned to a single agent for focused work.
/// It inherits context from its parent room.
/// </summary>
public record BreakoutRoom(
    string Id,
    string Name,
    string ParentRoomId,
    string AssignedAgentId,
    List<TaskItem> Tasks,
    RoomStatus Status,
    List<ChatEnvelope> RecentMessages,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

/// <summary>
/// A message exchanged in a collaboration room, including metadata for
/// routing, threading, and delivery prioritization.
/// </summary>
public record ChatEnvelope(
    string Id,
    string RoomId,
    string SenderId,
    string SenderName,
    string? SenderRole,
    MessageSenderKind SenderKind,
    MessageKind Kind,
    string Content,
    DateTime SentAt,
    string? CorrelationId = null,
    string? ReplyToMessageId = null,
    DeliveryHint? Hint = null
);

/// <summary>
/// Routing hints attached to a message for targeted delivery to specific
/// agents or roles, with priority and reply-requested semantics.
/// </summary>
public record DeliveryHint(
    string? TargetRole,
    string? TargetAgentId,
    DeliveryPriority Priority,
    bool ReplyRequested
);
