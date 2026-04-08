namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a message in a breakout room.
/// Maps to the "breakout_messages" table.
/// </summary>
public class BreakoutMessageEntity
{
    public string Id { get; set; } = string.Empty;
    public string BreakoutRoomId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string? SenderRole { get; set; }
    public string SenderKind { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public string? SessionId { get; set; }

    // Navigation properties
    public BreakoutRoomEntity BreakoutRoom { get; set; } = null!;
}
