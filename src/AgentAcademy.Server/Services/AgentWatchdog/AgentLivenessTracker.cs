using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services.AgentWatchdog;

/// <summary>
/// Default in-memory implementation of <see cref="IAgentLivenessTracker"/>.
/// All state lives in two ConcurrentDictionaries (turn map, session→turn map)
/// and is GC'd on process exit. No persistence — by design: the tracker is a
/// best-effort liveness oracle, not a source of truth.
/// </summary>
public sealed class AgentLivenessTracker : IAgentLivenessTracker
{
    private readonly TimeProvider _time;
    private readonly ILogger<AgentLivenessTracker> _logger;
    private readonly ConcurrentDictionary<string, TurnLiveness> _turns = new();
    private readonly ConcurrentDictionary<string, string> _sessionToTurn = new(StringComparer.Ordinal);

    public AgentLivenessTracker(TimeProvider time, ILogger<AgentLivenessTracker> logger)
    {
        _time = time;
        _logger = logger;
    }

    public IDisposable RegisterTurn(
        string turnId,
        string agentId,
        string agentName,
        string roomId,
        string? sprintId,
        CancellationTokenSource cts)
    {
        var now = _time.GetUtcNow();
        var entry = new TurnLiveness(turnId, agentId, agentName, roomId, sprintId, now, cts);
        if (!_turns.TryAdd(turnId, entry))
        {
            _logger.LogWarning("Liveness tracker collision on turnId {TurnId}; ignoring duplicate registration", turnId);
        }
        return new Registration(this, turnId);
    }

    public void NoteProgress(string turnId, string kind)
    {
        if (_turns.TryGetValue(turnId, out var entry))
        {
            var now = _time.GetUtcNow();
            entry.LastProgressAt = now;
            entry.LastEventAt = now;
            entry.LastEventKind = kind;
        }
    }

    public void NoteEvent(string turnId, string kind)
    {
        if (_turns.TryGetValue(turnId, out var entry))
        {
            entry.LastEventAt = _time.GetUtcNow();
            entry.LastEventKind = kind;
        }
    }

    public int IncrementDenial(string turnId, string kind)
    {
        if (_turns.TryGetValue(turnId, out var entry))
        {
            entry.LastEventAt = _time.GetUtcNow();
            entry.LastEventKind = "deny:" + kind;
            return Interlocked.Increment(ref entry.DenialCount);
        }
        return -1;
    }

    public void NoteProgressBySessionId(string? sessionId, string kind)
    {
        if (TryResolveTurnId(sessionId, out var turnId)) NoteProgress(turnId, kind);
    }

    public void NoteEventBySessionId(string? sessionId, string kind)
    {
        if (TryResolveTurnId(sessionId, out var turnId)) NoteEvent(turnId, kind);
    }

    public int IncrementDenialBySessionId(string? sessionId, string kind)
    {
        return TryResolveTurnId(sessionId, out var turnId) ? IncrementDenial(turnId, kind) : -1;
    }

    public void LinkSession(string sessionId, string turnId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        // Last-writer-wins: cached SDK sessions serve at most one turn at a
        // time (the sender's per-session SendLock guarantees serialization),
        // so overwriting any previous link is correct.
        _sessionToTurn[sessionId] = turnId;
    }

    public void UnlinkSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _sessionToTurn.TryRemove(sessionId, out _);
    }

    public IReadOnlyList<TurnDiagnostic> Snapshot()
    {
        var list = new List<TurnDiagnostic>(_turns.Count);
        foreach (var kv in _turns)
        {
            var e = kv.Value;
            list.Add(new TurnDiagnostic(
                e.TurnId, e.AgentId, e.AgentName, e.RoomId, e.SprintId,
                e.StartedAt, e.LastProgressAt, e.LastEventAt,
                Volatile.Read(ref e.DenialCount), e.LastEventKind, e.State));
        }
        return list;
    }

    public bool TryMarkStalledAndCancel(string turnId, string reason)
    {
        if (!_turns.TryGetValue(turnId, out var entry)) return false;

        // Atomic state transition Running → StallDetected. If another tick
        // already marked this turn (or the runner has completed it), bail.
        lock (entry.SyncRoot)
        {
            if (entry.State != TurnState.Running) return false;
            entry.State = TurnState.StallDetected;
        }

        try
        {
            entry.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Runner disposed the CTS just as we cancelled — race is benign.
            _logger.LogDebug("CTS already disposed for turn {TurnId} when watchdog cancelled (reason={Reason})", turnId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cancel turn {TurnId} (reason={Reason})", turnId, reason);
        }
        return true;
    }

    private bool TryResolveTurnId(string? sessionId, out string turnId)
    {
        if (sessionId is not null && _sessionToTurn.TryGetValue(sessionId, out var t))
        {
            turnId = t;
            return true;
        }
        turnId = string.Empty;
        return false;
    }

    private void Remove(string turnId)
    {
        if (_turns.TryRemove(turnId, out var entry))
        {
            // Mark Completed for any concurrent observer that holds a reference.
            lock (entry.SyncRoot) entry.State = TurnState.Completed;
        }
    }

    /// <summary>
    /// Mutable per-turn entry. Fields are accessed across threads — DenialCount
    /// uses Interlocked, timestamp fields are written atomically (DateTimeOffset
    /// fits in 16 bytes; .NET allows torn reads in theory but the watchdog is
    /// best-effort and the worst case is one extra scan tick).
    /// </summary>
    private sealed class TurnLiveness
    {
        public readonly string TurnId;
        public readonly string AgentId;
        public readonly string AgentName;
        public readonly string RoomId;
        public readonly string? SprintId;
        public readonly DateTimeOffset StartedAt;
        public readonly CancellationTokenSource Cts;
        public readonly object SyncRoot = new();

        public DateTimeOffset LastProgressAt;
        public DateTimeOffset LastEventAt;
        public string? LastEventKind;
        public int DenialCount;
        public TurnState State = TurnState.Running;

        public TurnLiveness(
            string turnId, string agentId, string agentName, string roomId,
            string? sprintId, DateTimeOffset startedAt, CancellationTokenSource cts)
        {
            TurnId = turnId;
            AgentId = agentId;
            AgentName = agentName;
            RoomId = roomId;
            SprintId = sprintId;
            StartedAt = startedAt;
            LastProgressAt = startedAt;
            LastEventAt = startedAt;
            Cts = cts;
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly AgentLivenessTracker _tracker;
        private readonly string _turnId;
        private int _disposed;

        public Registration(AgentLivenessTracker tracker, string turnId)
        {
            _tracker = tracker;
            _turnId = turnId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _tracker.Remove(_turnId);
        }
    }
}
