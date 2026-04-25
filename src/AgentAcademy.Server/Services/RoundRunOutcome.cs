namespace AgentAcademy.Server.Services;

/// <summary>
/// Result of a single trigger run through <see cref="ConversationRoundRunner.RunRoundsAsync"/>.
/// Consumed by the future <c>SelfDriveDecisionService</c> (P1.2 §13 step 5)
/// so the decision logic does not have to re-query state the runner already
/// knows.
///
/// Defined now so item §13 step 2 can land independently of the decision
/// service and the decision service's call sites are obvious by type.
/// </summary>
/// <param name="HadNonPassResponse">
/// True iff at least one inner round produced a non-PASS agent response.
/// Decision step 9 ("lastTriggerProducedNoNonPassResponses → IDLE") reads this.
/// </param>
/// <param name="InnerRoundsExecuted">
/// Number of inner rounds actually executed (0..MaxRoundsPerTrigger). Used
/// to bump <c>SprintEntity.RoundsThisSprint</c> / <c>RoundsThisStage</c>.
/// </param>
public readonly record struct RoundRunOutcome(bool HadNonPassResponse, int InnerRoundsExecuted)
{
    public static RoundRunOutcome Empty { get; } = new(false, 0);
}
