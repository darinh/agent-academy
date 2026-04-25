using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Executes queued conversation rounds for a room.
/// </summary>
public interface IConversationRoundRunner
{
    /// <summary>
    /// Runs one trigger cycle (up to the configured max rounds) for the room
    /// and returns a <see cref="RoundRunOutcome"/> that the
    /// <c>SelfDriveDecisionService</c> (P1.2 §13 step 5) consumes to decide
    /// whether to enqueue a self-drive continuation.
    /// </summary>
    /// <param name="roomId">The room whose queue item is being processed.</param>
    /// <param name="wasSelfDriveContinuation">
    /// True when this trigger originated from a <see cref="QueueItemKind.SystemContinuation"/>
    /// queue item (P1.2). Threaded through to <c>IncrementRoundCountersAsync</c> so
    /// <c>SprintEntity.SelfDriveContinuations</c> is bumped only when a
    /// self-drive continuation actually executes (per-execution accounting,
    /// not per-enqueue, so orphaned continuations do not skew the counter).
    /// </param>
    /// <param name="cancellationToken">Trigger CT — does not affect the
    /// post-loop counter bump (which uses CT.None to guarantee accounting
    /// for already-executed rounds).</param>
    Task<RoundRunOutcome> RunRoundsAsync(string roomId, bool wasSelfDriveContinuation = false, CancellationToken cancellationToken = default);
}
