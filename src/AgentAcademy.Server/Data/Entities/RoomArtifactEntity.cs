namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a file artifact produced by an agent in a room.
/// Append-only event log — each write_file, commit, or delete creates a new record.
/// Maps to the "room_artifacts" table.
/// </summary>
public class RoomArtifactEntity
{
    public int Id { get; set; }
    public string RoomId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Created | Updated | Committed | Deleted</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Set for commit operations; null for individual file writes.</summary>
    public string? CommitSha { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
