namespace AgentAcademy.Shared.Models;

/// <summary>
/// Snapshot of a collaboration room's current state, including participants
/// and recent message history.
/// </summary>
public record RoomSnapshot(
    string Id,
    string Name,
    string? Topic,
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

/// <summary>
/// Paginated response for room message queries (cursor-based).
/// </summary>
public record RoomMessagesResponse(
    List<ChatEnvelope> Messages,
    bool HasMore
);

/// <summary>
/// Snapshot of a conversation session (epoch) within a room.
/// Sessions are rotated when message count exceeds a threshold,
/// and archived with LLM-generated summaries for context continuity.
/// </summary>
public record ConversationSessionSnapshot(
    string Id,
    string RoomId,
    string RoomType,
    int SequenceNumber,
    string Status,
    string? Summary,
    int MessageCount,
    DateTime CreatedAt,
    DateTime? ArchivedAt
);

/// <summary>
/// Response for session list queries with pagination metadata.
/// </summary>
public record SessionListResponse(
    List<ConversationSessionSnapshot> Sessions,
    int TotalCount
);

/// <summary>
/// Aggregate statistics about conversation sessions.
/// </summary>
public record SessionStats(
    int TotalSessions,
    int ActiveSessions,
    int ArchivedSessions,
    int TotalMessages
);
