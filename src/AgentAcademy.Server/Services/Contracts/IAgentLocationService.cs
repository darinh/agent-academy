using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Manages agent presence tracking: querying locations and moving agents between rooms.
/// </summary>
public interface IAgentLocationService
{
    /// <summary>
    /// Returns all agent locations.
    /// </summary>
    Task<List<AgentLocation>> GetAgentLocationsAsync();

    /// <summary>
    /// Returns a single agent's location, or null if not tracked.
    /// </summary>
    Task<AgentLocation?> GetAgentLocationAsync(string agentId);

    /// <summary>
    /// Moves an agent to a new room/state.
    /// </summary>
    Task<AgentLocation> MoveAgentAsync(
        string agentId, string roomId, AgentState state, string? breakoutRoomId = null);
}
