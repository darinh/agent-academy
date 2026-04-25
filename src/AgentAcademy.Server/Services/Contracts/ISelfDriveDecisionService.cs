namespace AgentAcademy.Server.Services.Contracts;

using AgentAcademy.Server.Services;

/// <summary>
/// P1.2 Self-Drive: Decides whether the orchestrator should enqueue a
/// SystemContinuation round after a conversation round trigger completes.
/// Implements the 12-branch decision tree from
/// <c>specs/100-product-vision/p1-2-self-drive-design.md</c> §4.6.
///
/// Called fail-open by <see cref="IConversationRoundRunner"/> after the
/// post-round counter bump. Decision-service failures must NEVER propagate
/// (the trigger that just ran has already succeeded).
/// </summary>
public interface ISelfDriveDecisionService
{
    /// <summary>
    /// Evaluates self-drive gates and either:
    ///   1. IDLEs (does nothing — the room waits for the next human or
    ///      external trigger),
    ///   2. HALTs by marking the sprint blocked (a cap was hit), or
    ///   3. Schedules a SystemContinuation enqueue (potentially with a
    ///      min-interval delay, fire-and-forget).
    /// </summary>
    /// <param name="roomId">Room whose round just completed.</param>
    /// <param name="capturedSprintId">
    ///   Sprint ID captured at the start of the FIRST executed round of
    ///   this trigger. Null if no sprint was active when the round
    ///   started — in which case self-drive does not apply (rounds ran
    ///   for a non-sprint reason).
    /// </param>
    /// <param name="outcome">Outcome of the just-completed round trigger.</param>
    /// <param name="ct">Caller's cancellation token (may be ignored — see impl).</param>
    Task DecideAsync(
        string roomId,
        string? capturedSprintId,
        RoundRunOutcome outcome,
        CancellationToken ct);
}
