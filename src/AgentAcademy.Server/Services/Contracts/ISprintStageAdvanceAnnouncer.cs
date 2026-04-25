using AgentAcademy.Server.Data.Entities;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Posts a coordination "stage changed" message into every active room of a
/// sprint's workspace and wakes the orchestrator so the team picks up the new
/// stage's intent without further human input. Mirrors
/// <see cref="ISprintKickoffService"/> but fires on stage transitions instead
/// of sprint creation. Closes the P1.3 gap from
/// <c>specs/100-product-vision/roadmap.md</c>.
/// </summary>
public interface ISprintStageAdvanceAnnouncer
{
    /// <summary>
    /// Posts the stage-transition message in each non-archived/non-completed
    /// room of <paramref name="sprint"/>'s workspace and calls
    /// <see cref="IAgentOrchestrator.HandleHumanMessage"/> for each so the
    /// orchestrator queues an agent response carrying the new stage's preamble.
    /// Failures are logged and never propagate — stage advancement must not
    /// fail because the announcement failed.
    /// </summary>
    /// <param name="sprint">
    /// The sprint that just transitioned. <c>CurrentStage</c> must already be
    /// the new stage (caller must invoke this AFTER persisting the change).
    /// </param>
    /// <param name="previousStage">The stage the sprint just left.</param>
    /// <param name="trigger">
    /// Optional trigger label (e.g. <c>"approved"</c>, <c>"forced"</c>) so
    /// agents and humans can tell why the transition happened.
    /// </param>
    /// <param name="targetRoomIds">
    /// Optional explicit set of room IDs to announce in. Required for the
    /// <c>Implementation → FinalSynthesis</c> transition: the stage sync
    /// marks rooms <c>Completed</c> before the announcer runs, which would
    /// otherwise hide them from the default workspace query. When null, the
    /// announcer falls back to "all non-archived, non-completed rooms in
    /// the workspace".
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The number of rooms in which an announcement message was posted. Zero
    /// is a valid outcome (e.g. workspace has no rooms yet).
    /// </returns>
    Task<int> AnnounceAsync(
        SprintEntity sprint,
        string previousStage,
        string? trigger = null,
        IReadOnlyCollection<string>? targetRoomIds = null,
        CancellationToken ct = default);
}
