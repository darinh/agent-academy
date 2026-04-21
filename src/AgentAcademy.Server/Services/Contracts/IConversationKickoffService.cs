namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for posting conversation kickoff messages in rooms.
/// </summary>
public interface IConversationKickoffService
{
    /// <summary>
    /// Posts a kickoff system message and triggers orchestration if the room
    /// has no human or agent messages yet. Returns true if kickoff was performed.
    /// </summary>
    Task<bool> TryKickoffAsync(
        string roomId, string? activeWorkspace, CancellationToken ct = default);
}
