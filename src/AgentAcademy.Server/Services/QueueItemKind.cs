namespace AgentAcademy.Server.Services;

/// <summary>
/// Distinguishes the origin of an item dispatched by <see cref="AgentOrchestrator"/>.
/// Threaded from <c>QueueItem</c> through <c>IOrchestratorDispatchService</c> down
/// to <c>IConversationRoundRunner</c> so per-sprint accounting can credit
/// self-drive continuations to <c>SprintEntity.SelfDriveContinuations</c>
/// without conflating them with human-triggered rounds.
/// </summary>
public enum QueueItemKind
{
    /// <summary>Trigger originated from a human posting in the room.</summary>
    HumanMessage = 0,

    /// <summary>Trigger was enqueued by <c>SelfDriveDecisionService</c> after the previous round-loop concluded with work still to do (P1.2).</summary>
    SystemContinuation = 1,
}
