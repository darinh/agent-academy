namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Wakes the orchestrator for every active room in a sprint's workspace so
/// the next agent round picks up freshly-applied state (self-eval preamble,
/// FinalSynthesis preamble, etc.). Extracted from
/// <c>SprintController.TryWakeOrchestratorForSprintAsync</c> so the
/// API-endpoint path AND the
/// <see cref="ISprintTerminalStageHandler"/> share one implementation. See
/// <c>specs/100-product-vision/sprint-terminal-stage-handler-design.md §4.2.1</c>.
/// </summary>
public interface IOrchestratorWakeService
{
    /// <summary>
    /// Enumerates non-Archived, non-Completed rooms in the sprint's
    /// workspace and dispatches a wake message to each via the orchestrator.
    /// Best-effort: per-room failures are logged as warnings and do not
    /// prevent other rooms from being woken. Outer try/catch swallows
    /// enumeration failures so the caller's flow is never disrupted.
    /// </summary>
    /// <param name="sprintId">Sprint whose workspace rooms should be woken.</param>
    /// <param name="ct">Cancellation token. Best-effort honored.</param>
    Task WakeWorkspaceRoomsForSprintAsync(string sprintId, CancellationToken ct = default);
}
