using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services.AgentWatchdog;

/// <summary>
/// Default <see cref="IWatchdogAgentRunner"/>: registers a turn with
/// <see cref="IAgentLivenessTracker"/> and forwards the call to
/// <see cref="IAgentExecutor.RunAsync"/>. Singleton — has no per-turn state of
/// its own (all turn state lives on the tracker).
/// </summary>
public sealed class WatchdogAgentRunner : IWatchdogAgentRunner
{
    private readonly IAgentExecutor _executor;
    private readonly IAgentLivenessTracker _livenessTracker;
    private readonly ILogger<WatchdogAgentRunner> _logger;

    public WatchdogAgentRunner(
        IAgentExecutor executor,
        IAgentLivenessTracker livenessTracker,
        ILogger<WatchdogAgentRunner> logger)
    {
        _executor = executor;
        _livenessTracker = livenessTracker;
        _logger = logger;
    }

    public async Task<string> RunAsync(
        AgentDefinition agent,
        string prompt,
        string? roomId,
        string? sprintId = null,
        string? workspacePath = null,
        CancellationToken cancellationToken = default)
    {
        // Per-turn correlation id and linked CTS — same protocol as
        // AgentTurnRunner. trackedRoomId substitutes a stable synthetic value
        // when no real roomId exists so the tracker snapshot still attributes
        // the turn to its agent.
        var turnId = Guid.NewGuid().ToString("N");
        var trackedRoomId = roomId ?? $"out-of-band:{agent.Id}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = _livenessTracker.RegisterTurn(
            turnId, agent.Id, agent.Name, trackedRoomId, sprintId, cts);

        try
        {
            return await _executor.RunAsync(agent, prompt, roomId, workspacePath, cts.Token, turnId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Outer cancellation — caller-driven. Propagate so caller can
            // distinguish from the watchdog-induced empty-response path.
            throw;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Watchdog-induced cancellation. Watchdog has already logged the
            // STALL REPORT and posted any room-level notice it wants. We
            // surface an empty string so existing caller try/catches treat it
            // identically to a no-op response.
            _logger.LogInformation(
                "Watchdog cancelled out-of-band turn {TurnId} for agent {AgentName} (room={RoomId})",
                turnId, agent.Name, trackedRoomId);
            return "";
        }
    }
}
