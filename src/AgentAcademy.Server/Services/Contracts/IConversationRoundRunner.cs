namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Executes queued conversation rounds for a room.
/// </summary>
public interface IConversationRoundRunner
{
    /// <summary>
    /// Runs one trigger cycle (up to the configured max rounds) for the room.
    /// </summary>
    Task RunRoundsAsync(string roomId, CancellationToken cancellationToken = default);
}
