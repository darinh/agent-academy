using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Routes queue items to either DM handling or room-round execution.
/// Extracted from AgentOrchestrator to isolate dispatch branching.
/// </summary>
public sealed class OrchestratorDispatchService : IOrchestratorDispatchService
{
    private readonly IConversationRoundRunner _roundRunner;
    private readonly IDirectMessageRouter _dmRouter;

    public OrchestratorDispatchService(
        IConversationRoundRunner roundRunner,
        IDirectMessageRouter dmRouter)
    {
        _roundRunner = roundRunner;
        _dmRouter = dmRouter;
    }

    public Task DispatchAsync(
        string roomId,
        string? targetAgentId,
        QueueItemKind kind = QueueItemKind.HumanMessage,
        CancellationToken cancellationToken = default)
    {
        if (targetAgentId is { } recipientAgentId)
        {
            return _dmRouter.RouteAsync(recipientAgentId);
        }

        return _roundRunner.RunRoundsAsync(
            roomId,
            wasSelfDriveContinuation: kind == QueueItemKind.SystemContinuation,
            cancellationToken: cancellationToken);
    }
}
