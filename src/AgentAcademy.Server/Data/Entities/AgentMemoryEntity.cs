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
}
