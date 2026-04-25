namespace AgentAcademy.Server.Services.AgentWatchdog;

/// <summary>
/// Read-only snapshot of one in-flight agent turn for watchdog inspection.
/// Carries no mutable state and no <see cref="System.Threading.CancellationTokenSource"/> —
/// callers cancel exclusively through
/// <see cref="IAgentLivenessTracker.TryMarkStalledAndCancel(string, string)"/>.
/// </summary>
public sealed record TurnDiagnostic(
    string TurnId,
    string AgentId,
    string AgentName,
    string RoomId,
    string? SprintId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastProgressAt,
    DateTimeOffset LastEventAt,
    int DenialCount,
    string? LastEventKind,
    TurnState State);

/// <summary>
/// Lifecycle state of a tracked turn. Watchdog uses these to ensure cancel +
/// invalidate fire at most once per turn even across multiple scan ticks.
/// </summary>
public enum TurnState
{
    /// <summary>Turn is in flight; watchdog is monitoring.</summary>
    Running,
    /// <summary>Watchdog has decided to cancel this turn; cancel has been signaled.</summary>
    StallDetected,
    /// <summary>Runner has disposed the registration; entry will be removed shortly.</summary>
    Completed,
}
