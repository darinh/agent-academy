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
    /// When <paramref name="turnId"/> is provided, the session is linked to
    /// the watchdog liveness tracker for the duration of the call so SDK
    /// callbacks (permission requests, streaming events) can be attributed
    /// to the correct in-flight turn.
    /// </summary>
    Task<string> SendWithRetryAsync(
        CopilotSession session,
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct,
        string? turnId = null);

    /// <summary>
    /// Sends a prompt and collects the complete streamed response.
    /// Used for both normal sends and session priming.
    /// </summary>
    Task<string> CollectResponseAsync(
        CopilotSession session,
        string prompt,
        string agentId,
        string? roomId,
        CancellationToken ct,
        string? turnId = null);
}
