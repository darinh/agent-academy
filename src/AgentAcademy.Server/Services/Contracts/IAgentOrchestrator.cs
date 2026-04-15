namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Queue-based message processor that serializes agent work. Accepts human
/// messages and DMs, enqueues them, and dispatches to the appropriate handler
/// for execution.
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>
    /// Enqueues a room for processing after a human message arrives.
    /// Processing is serialized — only one room is handled at a time.
    /// </summary>
    void HandleHumanMessage(string roomId);

    /// <summary>
    /// Triggers an immediate round for a specific agent after receiving a DM.
    /// Finds the agent's current room and runs only that agent.
    /// </summary>
    void HandleDirectMessage(string recipientAgentId);

    /// <summary>
    /// Scans for rooms with unanswered human messages and re-enqueues them.
    /// Call on every startup to recover queue state lost during shutdown or crash.
    /// </summary>
    Task ReconstructQueueAsync();

    /// <summary>
    /// Runs crash recovery for the main room on startup if a crash was detected.
    /// </summary>
    Task HandleStartupRecoveryAsync(string mainRoomId);
}
