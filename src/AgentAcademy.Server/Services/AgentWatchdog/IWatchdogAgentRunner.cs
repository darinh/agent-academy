using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.AgentWatchdog;

/// <summary>
/// Wraps <see cref="IAgentExecutor.RunAsync"/> with the watchdog liveness
/// registration boilerplate used by <c>AgentTurnRunner</c>. Direct callers of
/// <see cref="IAgentExecutor.RunAsync"/> outside the conversation round loop
/// (retrospective, learning digest, conversation summarizer, breakout review)
/// should use this so their turns are registered with
/// <see cref="IAgentLivenessTracker"/> and become observable / cancellable by
/// <c>AgentWatchdogService</c>.
///
/// <para>
/// Cancellation semantics mirror <c>AgentTurnRunner.RunAgentTurnAsync</c>:
/// outer-token cancellation propagates as <see cref="OperationCanceledException"/>;
/// watchdog-induced cancellation is swallowed and returned as the empty string
/// so callers can decide how to surface a stalled call to their domain.
/// </para>
/// </summary>
public interface IWatchdogAgentRunner
{
    /// <summary>
    /// Generates a fresh <c>turnId</c>, registers the turn with the liveness
    /// tracker, links a CTS, and invokes <see cref="IAgentExecutor.RunAsync"/>.
    /// </summary>
    /// <param name="agent">Effective agent definition (already cloned/resolved by caller).</param>
    /// <param name="prompt">Prompt text passed to the SDK.</param>
    /// <param name="roomId">
    /// Optional room identifier. <c>null</c> for system-internal calls (e.g.,
    /// the conversation summarizer); the tracker entry will use a synthetic
    /// <c>out-of-band:{agentId}</c> identifier so diagnostics still attribute
    /// the turn to the right agent.
    /// </param>
    /// <param name="sprintId">Optional sprint identifier for diagnostics.</param>
    /// <param name="workspacePath">Optional worktree path for the agent process.</param>
    /// <param name="cancellationToken">Outer cancellation token; propagates if signalled.</param>
    /// <returns>The agent's response, or the empty string when the watchdog cancelled the turn.</returns>
    Task<string> RunAsync(
        AgentDefinition agent,
        string prompt,
        string? roomId,
        string? sprintId = null,
        string? workspacePath = null,
        CancellationToken cancellationToken = default);
}
