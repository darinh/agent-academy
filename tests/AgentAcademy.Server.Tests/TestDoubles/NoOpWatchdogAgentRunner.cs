using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.AgentWatchdog;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests.TestDoubles;

/// <summary>
/// Minimal pass-through <see cref="IWatchdogAgentRunner"/> for tests that
/// don't exercise the watchdog. Forwards directly to the supplied
/// <see cref="IAgentExecutor"/> with no liveness registration. Cancellation
/// passes through; quota / cancellation exceptions surface to the caller
/// unchanged so the wrapped service's existing try/catch still drives.
/// </summary>
internal sealed class NoOpWatchdogAgentRunner : IWatchdogAgentRunner
{
    private readonly IAgentExecutor _executor;

    public NoOpWatchdogAgentRunner(IAgentExecutor executor)
    {
        _executor = executor;
    }

    public Task<string> RunAsync(
        AgentDefinition agent,
        string prompt,
        string? roomId,
        string? sprintId = null,
        string? workspacePath = null,
        CancellationToken cancellationToken = default)
        => _executor.RunAsync(agent, prompt, roomId, workspacePath, cancellationToken, turnId: null);
}
