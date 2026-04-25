using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Executes queued conversation rounds for a room.
/// </summary>
public interface IConversationRoundRunner
{
    /// <summary>
    /// Runs one trigger cycle (up to the configured max rounds) for the room
    /// and returns a <see cref="RoundRunOutcome"/> that the upcoming
    /// <c>SelfDriveDecisionService</c> (P1.2 §13 step 5) consumes to decide
    /// whether to enqueue a self-drive continuation.
    /// </summary>
    Task<RoundRunOutcome> RunRoundsAsync(string roomId, CancellationToken cancellationToken = default);
}
