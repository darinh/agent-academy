using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Loads agent memories from the database, including shared memories and expiry filtering.
/// </summary>
public interface IAgentMemoryLoader
{
    /// <summary>
    /// Loads all active memories for the given agent (own + shared, excluding expired).
    /// </summary>
    Task<List<AgentMemory>> LoadAsync(string agentId);
}
