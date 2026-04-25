using AgentAcademy.Server.Data.Entities;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Posts a coordination "sprint started" message into every active room of a
/// sprint's workspace and triggers the orchestrator so the team picks it up
/// without further human input. Closes G2 from
/// <c>specs/100-product-vision/gap-analysis.md</c>.
/// </summary>
public interface ISprintKickoffService
{
    /// <summary>
    /// Posts the kickoff message in each non-archived/non-completed room of
    /// <paramref name="sprint"/>'s workspace and calls
    /// <see cref="IAgentOrchestrator.HandleHumanMessage"/> for each so the
    /// orchestrator queues an agent response. Failures are logged and never
    /// propagate — sprint creation must not fail because the kickoff failed.
    /// </summary>
    /// <param name="sprint">The newly created sprint.</param>
    /// <param name="trigger">
    /// Optional trigger label (e.g. <c>"auto"</c>, <c>"scheduled"</c>) included
    /// in the message body so agents and humans can tell why the sprint started.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The number of rooms in which a kickoff message was posted. Zero is a
    /// valid outcome (e.g. workspace has no rooms yet).
    /// </returns>
    Task<int> PostKickoffAsync(SprintEntity sprint, string? trigger = null, CancellationToken ct = default);
}
