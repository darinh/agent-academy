using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Fallback executor that returns a clear offline error when the
/// Copilot SDK is unavailable. Prevents misleading users with
/// realistic-sounding fake responses.
/// </summary>
public sealed class StubExecutor : IAgentExecutor
{
    private readonly ILogger<StubExecutor> _logger;

    public StubExecutor(ILogger<StubExecutor> logger)
    {
        _logger = logger;
        _logger.LogWarning("StubExecutor active — agents will return offline notices");
    }

    public bool IsFullyOperational => false;
    public bool IsAuthFailed => false;

    public Task<string> RunAsync(
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var response = $"⚠️ Agent **{agent.Name}** ({agent.Role}) is offline — " +
                       "the Copilot SDK is not connected. " +
                       "Log in via GitHub OAuth or check server logs to activate.";

        _logger.LogDebug(
            "Stub offline notice for {AgentId} ({Role}) in room {RoomId}",
            agent.Id, agent.Role, roomId ?? "none");

        return Task.FromResult(response);
    }

    public Task InvalidateSessionAsync(string agentId, string? roomId)
    {
        return Task.CompletedTask;
    }

    public Task InvalidateRoomSessionsAsync(string roomId)
    {
        return Task.CompletedTask;
    }

    public Task InvalidateAllSessionsAsync()
    {
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}
