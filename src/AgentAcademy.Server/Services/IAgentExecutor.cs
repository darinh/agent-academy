using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Abstraction for running an agent against a prompt and returning
/// the complete response text. Implementations manage session lifecycle,
/// model selection, and streaming aggregation internally.
/// </summary>
public interface IAgentExecutor
{
    /// <summary>
    /// True when backed by a real LLM provider (e.g., Copilot SDK).
    /// False for stub/mock implementations that return canned responses.
    /// </summary>
    bool IsFullyOperational { get; }

    /// <summary>
    /// True when the executor has detected an authentication failure that
    /// requires the user to re-authenticate before Copilot can recover.
    /// </summary>
    bool IsAuthFailed { get; }

    /// <summary>
    /// Marks the executor as auth-degraded and emits any transition-side effects
    /// (room notice, notifications) if this is the first transition into failure.
    /// </summary>
    Task MarkAuthDegradedAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks the executor as auth-operational again and emits recovery side effects
    /// only when transitioning from degraded to healthy.
    /// </summary>
    Task MarkAuthOperationalAsync(CancellationToken ct = default);

    /// <summary>
     /// Sends <paramref name="prompt"/> to the agent and returns the
    /// complete response. The implementation may stream internally but
    /// this method returns only after the full response is assembled.
    /// </summary>
    Task<string> RunAsync(
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct = default);

    /// <summary>
    /// Invalidates (disposes) the cached session for a specific agent
    /// in a specific room. The next <see cref="RunAsync"/> call will
    /// create a fresh session.
    /// </summary>
    Task InvalidateSessionAsync(string agentId, string? roomId);

    /// <summary>
    /// Invalidates all cached sessions associated with a room.
    /// Called when a room is closed or reset.
    /// </summary>
    Task InvalidateRoomSessionsAsync(string roomId);

    /// <summary>
    /// Invalidates all cached sessions across all rooms and agents.
    /// Called on project/workspace switch to give agents a clean slate.
    /// </summary>
    Task InvalidateAllSessionsAsync();

    /// <summary>
    /// Releases all managed resources (sessions, client connections).
    /// </summary>
    Task DisposeAsync();
}
