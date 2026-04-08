namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a chat message in a room.
/// Maps to the "messages" table.
/// </summary>
public class MessageEntity
{
    public string Id { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string? SenderRole { get; set; }
    public string SenderKind { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public string? RecipientId { get; set; }
    public string? CorrelationId { get; set; }
    public string? ReplyToMessageId { get; set; }
    public string? SessionId { get; set; }
    public DateTime? AcknowledgedAt { get; set; }

    // Navigation properties
    public RoomEntity Room { get; set; } = null!;
}
