namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Runs automated post-task retrospectives for agents after task merge.
/// </summary>
public interface IRetrospectiveService
{
    /// <summary>
    /// Runs a retrospective for the completed task. Safe to call fire-and-forget.
    /// </summary>
    Task RunRetrospectiveAsync(string taskId, string? assignedAgentId);
}
