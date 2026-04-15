using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for room/breakout plan content management.
/// </summary>
public interface IPlanService
{
    /// <summary>
    /// Returns the plan content for a room, or null if none exists.
    /// </summary>
    Task<PlanContent?> GetPlanAsync(string roomId);

    /// <summary>
    /// Creates or updates the plan for a room.
    /// </summary>
    Task SetPlanAsync(string roomId, string content);

    /// <summary>
    /// Deletes the plan for a room. Returns true if a plan was deleted.
    /// </summary>
    Task<bool> DeletePlanAsync(string roomId);
}
