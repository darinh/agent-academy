namespace AgentAcademy.Server.Services;

/// <summary>
/// Configuration for the sprint terminal-stage ceremony driver. Bound to the
/// <c>Orchestrator:TerminalStage</c> section in <c>appsettings.json</c>.
/// See <c>specs/100-product-vision/sprint-terminal-stage-handler-design.md §5.3</c>.
/// </summary>
public sealed class TerminalStageOptions
{
    public const string SectionName = "Orchestrator:TerminalStage";

    /// <summary>
    /// Maximum minutes a sprint may stay at FinalSynthesis without producing
    /// a <c>SprintReport</c> artifact before the driver auto-blocks it with
    /// <c>"Terminal-stage ceremony failed: SprintReport not produced within
    /// {N} minutes of entering FinalSynthesis."</c>. Default 30. Set to 0 or
    /// negative to disable the watchdog (not recommended in production).
    /// </summary>
    public int FinalSynthesisStallMinutes { get; set; } = 30;

    /// <summary>
    /// Maximum minutes a sprint may stay in <c>SelfEvaluationInFlight=true</c>
    /// without a verdict landing before the driver auto-blocks it with
    /// <c>"Terminal-stage ceremony failed: SelfEvaluationReport not produced
    /// within {N} minutes of self-eval start."</c>. Default 15 (shorter than
    /// FinalSynthesis because the self-eval prompt is mechanical PASS/FAIL/UNVERIFIED
    /// — half an hour without a report indicates the team is genuinely stuck).
    /// Set to 0 or negative to disable.
    /// </summary>
    public int SelfEvalStallMinutes { get; set; } = 15;
}
