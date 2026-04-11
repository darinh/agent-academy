namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Tracks logical conversation session boundaries within a room.
/// When message count exceeds a threshold, the session is archived
/// with an LLM-generated summary and a new session begins.
/// Maps to the "conversation_sessions" table.
/// </summary>
public class ConversationSessionEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RoomId { get; set; } = string.Empty;
    public string? WorkspacePath { get; set; }
    public string RoomType { get; set; } = "Main"; // "Main" | "Breakout"
    public int SequenceNumber { get; set; } = 1;
    public string Status { get; set; } = "Active"; // "Active" | "Archived"
    public string? Summary { get; set; }
    public int MessageCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }

    // Sprint scoping
    public string? SprintId { get; set; }
    public string? SprintStage { get; set; }

    // Navigation
    public SprintEntity? Sprint { get; set; }
}
