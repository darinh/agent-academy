namespace AgentAcademy.Server.Services;

/// <summary>
/// Configuration for the sprint self-evaluation ceremony (P1.4).
/// Bound to the <c>Orchestrator:SelfEval</c> section in
/// <c>appsettings.json</c>. See
/// <c>specs/100-product-vision/p1-4-self-evaluation-design.md</c>.
/// </summary>
public sealed class SelfEvalOptions
{
    public const string SectionName = "Orchestrator:SelfEval";

    /// <summary>
    /// Maximum number of self-evaluation submissions allowed at the
    /// Implementation stage before the sprint is auto-blocked. The first
    /// submission counts as attempt 1; the cap-th non-AllPass submission
    /// blocks the sprint with reason
    /// <c>"Self-eval failed N times — human input required"</c>.
    /// </summary>
    public int MaxSelfEvalAttempts { get; set; } = 3;
}
