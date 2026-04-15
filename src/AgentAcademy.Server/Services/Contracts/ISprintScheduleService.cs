using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for sprint schedule CRUD operations.
/// </summary>
public interface ISprintScheduleService
{
    /// <summary>
    /// Gets the sprint schedule for a workspace, or null if none exists.
    /// </summary>
    Task<SprintScheduleResponse?> GetScheduleAsync(string workspacePath);

    /// <summary>
    /// Creates or updates the sprint schedule for a workspace.
    /// </summary>
    Task<SprintScheduleResponse> UpsertScheduleAsync(
        string workspacePath, string cronExpression, string timeZoneId, bool enabled);

    /// <summary>
    /// Deletes the sprint schedule for a workspace.
    /// Returns false if no schedule existed.
    /// </summary>
    Task<bool> DeleteScheduleAsync(string workspacePath);
}
