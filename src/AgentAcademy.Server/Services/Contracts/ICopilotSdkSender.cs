using AgentAcademy.Shared.Models;
using GitHub.Copilot.SDK;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for sending prompts to a Copilot session with retry logic.
/// </summary>
public interface ICopilotSdkSender
{
    /// <summary>
    /// Sends a prompt and returns the complete response, retrying on
    /// transient and quota errors. Auth errors are never retried.
    /// </summary>
    Task<string> SendWithRetryAsync(
        CopilotSession session,
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct);

    /// <summary>
    /// Sends a prompt and collects the complete streamed response.
    /// Used for both normal sends and session priming.
    /// </summary>
    Task<string> CollectResponseAsync(
        CopilotSession session,
        string prompt,
        string agentId,
        string? roomId,
        CancellationToken ct);
}
