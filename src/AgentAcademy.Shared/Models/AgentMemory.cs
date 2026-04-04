namespace AgentAcademy.Shared.Models;

/// <summary>
/// A single memory entry stored by an agent.
/// Memories persist across sessions and are injected into agent prompts.
/// </summary>
public record AgentMemory(
    string AgentId,
    string Category,
    string Key,
    string Value,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? LastAccessedAt = null,
    DateTime? ExpiresAt = null
);
