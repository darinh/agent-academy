namespace AgentAcademy.Server.Services.AgentWatchdog;

/// <summary>
/// Configuration for the agent watchdog (silent-stall detector).
/// Bound to the <c>Orchestrator:AgentWatchdog</c> section in <c>appsettings.json</c>.
///
/// The watchdog scans every <see cref="ScanIntervalSeconds"/> and cancels any
/// in-flight agent turn that has gone quiet for longer than
/// <see cref="StallThresholdSeconds"/> OR has accumulated at least
/// <see cref="MaxDenialsPerTurn"/> SDK permission denials. Both triggers cover
/// the silent-stall failure mode where the SDK retries denied permission
/// requests until its internal budget is exhausted and the turn never
/// completes.
///
/// Defaults are conservative: a 90s quiet threshold tolerates legitimate long
/// thinking/tool calls, and a 10-denial cap matches the expected ceiling of
/// the SDK's per-turn retry budget without false positives during a single
/// approve/deny mismatch.
/// </summary>
public sealed class AgentWatchdogOptions
{
    public const string SectionName = "Orchestrator:AgentWatchdog";

    /// <summary>
    /// Global kill switch. When false, the watchdog hosted service spins on a
    /// short delay loop with no scanning side effects. Useful as a hot-flip
    /// mitigation if the watchdog itself starts misbehaving.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Number of seconds since the turn's last observable progress (assistant
    /// delta/message/usage event or approved permission request) before the
    /// turn is considered stalled. Permission denials do NOT count as
    /// progress — by design — so a denial storm cannot keep refreshing the
    /// stall timer indefinitely.
    /// </summary>
    public int StallThresholdSeconds { get; set; } = 90;

    /// <summary>
    /// How frequently the watchdog scans the liveness tracker. Smaller values
    /// detect stalls faster at the cost of CPU/log churn. Must be ≤
    /// <see cref="StallThresholdSeconds"/> (validated at startup).
    /// </summary>
    public int ScanIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Hard cap on accumulated permission denials per turn. When reached, the
    /// watchdog cancels the turn even if the timer has not elapsed —
    /// catches a denial-storm stall faster than the time-based trigger.
    /// Set to 0 to disable the denial-storm trigger entirely.
    /// </summary>
    public int MaxDenialsPerTurn { get; set; } = 10;

    /// <summary>
    /// When true, the watchdog posts a system message to the agent's room
    /// when it cancels a stalled turn so the human (and other agents) can
    /// see the recovery. Best-effort; failure to post never blocks the
    /// cancel + invalidate path.
    /// </summary>
    public bool PostStallNoticeToRoom { get; set; } = true;
}
