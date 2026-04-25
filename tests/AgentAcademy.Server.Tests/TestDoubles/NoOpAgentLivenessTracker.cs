using AgentAcademy.Server.Services.AgentWatchdog;

namespace AgentAcademy.Server.Tests.TestDoubles;

/// <summary>
/// Minimal no-op <see cref="IAgentLivenessTracker"/> for tests that don't
/// exercise the watchdog. Methods do nothing; <see cref="RegisterTurn"/>
/// returns a disposable that does nothing; <see cref="Snapshot"/> returns
/// an empty list.
/// </summary>
internal sealed class NoOpAgentLivenessTracker : IAgentLivenessTracker
{
    public IDisposable RegisterTurn(
        string turnId,
        string agentId,
        string agentName,
        string roomId,
        string? sprintId,
        CancellationTokenSource cts) => NoOpDisposable.Instance;

    public void NoteProgress(string turnId, string kind) { }
    public void NoteEvent(string turnId, string kind) { }
    public int IncrementDenial(string turnId, string kind) => 0;

    public void NoteProgressBySessionId(string? sessionId, string kind) { }
    public void NoteEventBySessionId(string? sessionId, string kind) { }
    public int IncrementDenialBySessionId(string? sessionId, string kind) => -1;

    public void LinkSession(string sessionId, string turnId) { }
    public void UnlinkSession(string sessionId) { }

    public IReadOnlyList<TurnDiagnostic> Snapshot() => Array.Empty<TurnDiagnostic>();

    public bool TryMarkStalledAndCancel(string turnId, string reason) => false;

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
