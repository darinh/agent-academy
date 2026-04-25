namespace AgentAcademy.Server.Services;

/// <summary>
/// Configuration for the orchestrator self-drive feature (P1.2).
/// Bound to the <c>Orchestrator:SelfDrive</c> section in <c>appsettings.json</c>.
/// All fields have safe defaults so missing config does not disable self-drive.
/// </summary>
public sealed class SelfDriveOptions
{
    public const string SectionName = "Orchestrator:SelfDrive";

    /// <summary>
    /// Global kill switch. When false, <c>SelfDriveDecisionService</c> is a
    /// logged no-op and the system reverts to pure trigger-driven behaviour.
    /// Useful in dev, in tests, and as a hot-flip mitigation in production.
    ///
    /// Defaults to <b>false</b> so tests that build DI without loading
    /// <c>appsettings.json</c> don't accidentally enable self-drive and
    /// generate orphan background tasks. Production enables it via the
    /// <c>Orchestrator:SelfDrive:Enabled</c> entry in <c>appsettings.json</c>.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Hard ceiling on total agent-turn rounds for a single sprint. When
    /// reached, the sprint is blocked with a <c>"Round cap reached"</c>
    /// reason and the existing P1.4 SprintBlocked → Discord NeedsInput
    /// pipeline surfaces the halt to the human.
    /// </summary>
    public int MaxRoundsPerSprint { get; set; } = 50;

    /// <summary>
    /// Per-stage round cap. A sprint stuck spinning in Planning should
    /// halt before burning the full sprint cap, surfacing the stuck-stage
    /// for human inspection. Counter resets on every stage advance.
    /// </summary>
    public int MaxRoundsPerStage { get; set; } = 20;

    /// <summary>
    /// Bounds runaway self-drive loops between human checkpoints.
    /// Counter resets on every stage advance and on every human-triggered
    /// round (since a human message implicitly approves continuation).
    /// </summary>
    public int MaxConsecutiveSelfDriveContinuations { get; set; } = 8;

    /// <summary>
    /// Backstop against a tight-loop bug enqueueing continuations as fast
    /// as the queue dispatches them. Implemented as a delay on the enqueue
    /// path (with re-check after the delay), not as an immediate IDLE
    /// gate — IDLE-on-recent-activity would block all self-drive because
    /// the round-runner just wrote LastRoundCompletedAt = now before the
    /// decision service ran.
    /// </summary>
    public int MinIntervalBetweenContinuationsMs { get; set; } = 2000;
}
