namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Dispatches queued orchestrator work to the appropriate execution path.
/// </summary>
public interface IOrchestratorDispatchService
{
    /// <summary>
    /// Routes a queue item to DM routing or room-round execution.
    /// <paramref name="kind"/> is threaded through to the round runner so
    /// per-sprint accounting can credit self-drive continuations to
    /// <c>SprintEntity.SelfDriveContinuations</c> (P1.2).
    /// </summary>
    Task DispatchAsync(
        string roomId,
        string? targetAgentId,
        QueueItemKind kind = QueueItemKind.HumanMessage,
        CancellationToken cancellationToken = default);
}
