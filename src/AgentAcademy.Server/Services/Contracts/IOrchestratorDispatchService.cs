namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Dispatches queued orchestrator work to the appropriate execution path.
/// </summary>
public interface IOrchestratorDispatchService
{
    /// <summary>
    /// Routes a queue item to DM routing or room-round execution.
    /// </summary>
    Task DispatchAsync(string roomId, string? targetAgentId, CancellationToken cancellationToken = default);
}
