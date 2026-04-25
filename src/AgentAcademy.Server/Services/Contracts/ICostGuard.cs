using AgentAcademy.Server.Data.Entities;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Cost-cap insertion point for the self-drive decision service
/// (<see cref="ISelfDriveDecisionService"/> when it lands in P1.2 §13 step 5).
/// Reserved hook from <c>p1-2-self-drive-design.md</c> §4.6 so the future
/// cost-tracking implementation lands as a DI swap, not a decision-tree
/// restructure.
///
/// The default registration is <see cref="NoOpCostGuard"/> which always
/// returns <c>false</c>; the real implementation arrives with the
/// cost-tracking design (<c>cost-tracking-design.md</c>) once token-counting
/// infrastructure exists.
/// </summary>
public interface ICostGuard
{
    /// <summary>
    /// Returns <c>true</c> if the orchestrator should halt the sprint
    /// (block via <c>SprintService.MarkSprintBlockedAsync</c>) before
    /// enqueueing the next self-drive continuation.
    /// </summary>
    Task<bool> ShouldHaltAsync(SprintEntity sprint, CancellationToken cancellationToken = default);
}
