namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for per-agent memory.
/// Agents store learned knowledge that survives across sessions.
/// Maps to the "agent_memories" table.
/// </summary>
public class AgentMemoryEntity
{
    public string AgentId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Last time this memory was read (via RECALL, LIST_MEMORIES, or prompt injection).
    /// Used for staleness detection. Null means never accessed since tracking was added.
    /// </summary>
    public DateTime? LastAccessedAt { get; set; }

    /// <summary>
    /// Optional expiration timestamp. When set, the memory is considered expired
    /// after this time and excluded from reads. Null means no expiry.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}
