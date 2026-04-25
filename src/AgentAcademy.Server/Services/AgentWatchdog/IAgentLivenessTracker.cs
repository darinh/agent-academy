namespace AgentAcademy.Server.Services.AgentWatchdog;

/// <summary>
/// In-memory liveness map for in-flight agent turns. Singleton; updates are
/// thread-safe (atomic field swaps + ConcurrentDictionary).
///
/// Two separate timestamps are tracked:
/// <list type="bullet">
///   <item><c>LastProgressAt</c> — bumped only on observable model/tool
///   <i>forward progress</i> (assistant deltas/messages/usage, approved
///   permission requests). The watchdog's time-based stall trigger uses
///   this. Permission denials deliberately do NOT bump it so a denial
///   storm cannot indefinitely defer a stall verdict.</item>
///   <item><c>LastEventAt</c> — bumped on any SDK callback we observe,
///   including denials. Used for diagnostics only.</item>
/// </list>
///
/// A separate session→turn map handles the case where the SDK callback only
/// gives us its own <c>SessionId</c> (string). Cached <see cref="GitHub.Copilot.SDK.CopilotSession"/>s
/// can be reused across multiple agent turns, so the link must be re-established
/// per send. The sender calls
/// <see cref="LinkSession(string, string)"/> immediately before
/// <c>SendAsync</c> and <see cref="UnlinkSession(string)"/> in <c>finally</c>.
/// </summary>
public interface IAgentLivenessTracker
{
    /// <summary>
    /// Registers an in-flight turn. The returned <see cref="IDisposable"/>
    /// marks the entry <see cref="TurnState.Completed"/> and removes it from
    /// the tracker. Must be wrapped in <c>using</c> so completion is recorded
    /// even on exception. Disposing twice is safe (idempotent).
    /// </summary>
    IDisposable RegisterTurn(
        string turnId,
        string agentId,
        string agentName,
        string roomId,
        string? sprintId,
        CancellationTokenSource cts);

    /// <summary>Bumps both LastProgressAt and LastEventAt. Use for forward progress.</summary>
    void NoteProgress(string turnId, string kind);

    /// <summary>Bumps only LastEventAt. Use for non-progress events (e.g., permission denials).</summary>
    void NoteEvent(string turnId, string kind);

    /// <summary>
    /// Increments DenialCount and bumps LastEventAt. Returns the new denial
    /// count, or -1 if the turn is not registered.
    /// </summary>
    int IncrementDenial(string turnId, string kind);

    /// <summary>
    /// Indirect variants keyed by SDK session id — used by the SDK permission
    /// callback which only sees the SDK <c>SessionId</c>. No-op (or returns -1)
    /// when no link has been established for the session.
    /// </summary>
    void NoteProgressBySessionId(string? sessionId, string kind);
    /// <inheritdoc cref="NoteProgressBySessionId(string?, string)"/>
    void NoteEventBySessionId(string? sessionId, string kind);
    /// <inheritdoc cref="NoteProgressBySessionId(string?, string)"/>
    int IncrementDenialBySessionId(string? sessionId, string kind);

    /// <summary>
    /// Associates a SDK session id with the currently in-flight turn. Called
    /// by the sender immediately before <c>SendAsync</c>. Overwrites any
    /// previous link for the same session — safe because cached sessions
    /// serve one turn at a time.
    /// </summary>
    void LinkSession(string sessionId, string turnId);

    /// <summary>Removes a session→turn link. Safe if no link exists.</summary>
    void UnlinkSession(string sessionId);

    /// <summary>Read-only snapshot of all tracked turns. No mutable state exposed.</summary>
    IReadOnlyList<TurnDiagnostic> Snapshot();

    /// <summary>
    /// Atomically transitions the turn from <see cref="TurnState.Running"/> to
    /// <see cref="TurnState.StallDetected"/> and signals cancellation on its
    /// CTS exactly once. Returns false if the turn is unknown or already
    /// stalled — guarantees idempotency across watchdog ticks.
    /// Does NOT remove the turn from the tracker; the runner's registration
    /// dispose is the only remove path.
    /// </summary>
    bool TryMarkStalledAndCancel(string turnId, string reason);
}
