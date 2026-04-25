using AgentAcademy.Server.Services.AgentWatchdog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="AgentLivenessTracker"/> — the in-memory
/// liveness map that backs the watchdog.
/// </summary>
public sealed class AgentLivenessTrackerTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly AgentLivenessTracker _tracker;

    public AgentLivenessTrackerTests()
    {
        _tracker = new AgentLivenessTracker(_time, NullLogger<AgentLivenessTracker>.Instance);
    }

    [Fact]
    public void RegisterTurn_ThenDispose_RemovesFromSnapshot()
    {
        var cts = new CancellationTokenSource();
        var reg = _tracker.RegisterTurn("t1", "agent1", "Agent One", "room1", "sprint1", cts);

        Assert.Single(_tracker.Snapshot());
        Assert.Equal("t1", _tracker.Snapshot()[0].TurnId);
        Assert.Equal(TurnState.Running, _tracker.Snapshot()[0].State);

        reg.Dispose();
        Assert.Empty(_tracker.Snapshot());
    }

    [Fact]
    public void Registration_DisposedTwice_IsIdempotent()
    {
        var cts = new CancellationTokenSource();
        var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);

        reg.Dispose();
        reg.Dispose(); // must not throw

        Assert.Empty(_tracker.Snapshot());
    }

    [Fact]
    public void NoteProgress_BumpsBothTimestamps()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        var initialProgress = _tracker.Snapshot()[0].LastProgressAt;

        _time.Advance(TimeSpan.FromSeconds(5));
        _tracker.NoteProgress("t1", "delta");

        var snap = _tracker.Snapshot()[0];
        Assert.True(snap.LastProgressAt > initialProgress);
        Assert.Equal(snap.LastProgressAt, snap.LastEventAt);
        Assert.Equal("delta", snap.LastEventKind);
    }

    [Fact]
    public void NoteEvent_BumpsOnlyLastEvent_NotProgress()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        var initialProgress = _tracker.Snapshot()[0].LastProgressAt;

        _time.Advance(TimeSpan.FromSeconds(7));
        _tracker.NoteEvent("t1", "perm-deny");

        var snap = _tracker.Snapshot()[0];
        Assert.Equal(initialProgress, snap.LastProgressAt); // unchanged
        Assert.True(snap.LastEventAt > initialProgress);
        Assert.Equal("perm-deny", snap.LastEventKind);
    }

    [Fact]
    public void IncrementDenial_IncrementsCount_DoesNotBumpProgress()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        var initialProgress = _tracker.Snapshot()[0].LastProgressAt;

        _time.Advance(TimeSpan.FromSeconds(1));
        var c1 = _tracker.IncrementDenial("t1", "shell");
        var c2 = _tracker.IncrementDenial("t1", "url");

        Assert.Equal(1, c1);
        Assert.Equal(2, c2);
        var snap = _tracker.Snapshot()[0];
        Assert.Equal(2, snap.DenialCount);
        Assert.Equal(initialProgress, snap.LastProgressAt); // not touched
        Assert.True(snap.LastEventAt > initialProgress);
    }

    [Fact]
    public void IncrementDenial_UnknownTurn_ReturnsMinusOne()
    {
        Assert.Equal(-1, _tracker.IncrementDenial("nope", "shell"));
    }

    [Fact]
    public void BySessionId_NoLink_AreNoOps()
    {
        // No exception, no state change, IncrementDenial returns -1.
        _tracker.NoteProgressBySessionId("missing", "x");
        _tracker.NoteEventBySessionId("missing", "x");
        Assert.Equal(-1, _tracker.IncrementDenialBySessionId("missing", "x"));
        Assert.Equal(-1, _tracker.IncrementDenialBySessionId(null, "x"));
    }

    [Fact]
    public void LinkSession_ThenBySessionId_RoutesToTurn()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        _tracker.LinkSession("sess-1", "t1");

        _time.Advance(TimeSpan.FromSeconds(2));
        _tracker.NoteProgressBySessionId("sess-1", "delta");
        var d1 = _tracker.IncrementDenialBySessionId("sess-1", "shell");
        var d2 = _tracker.IncrementDenialBySessionId("sess-1", "url");
        Assert.Equal(1, d1);
        Assert.Equal(2, d2);

        var snap = _tracker.Snapshot()[0];
        Assert.Equal(2, snap.DenialCount);
    }

    [Fact]
    public void LinkSession_OverwriteAttributesToNewTurn()
    {
        var cts1 = new CancellationTokenSource();
        var cts2 = new CancellationTokenSource();
        using var r1 = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts1);
        using var r2 = _tracker.RegisterTurn("t2", "a", "A", "r", null, cts2);

        _tracker.LinkSession("sess", "t1");
        _tracker.IncrementDenialBySessionId("sess", "x"); // → t1

        _tracker.LinkSession("sess", "t2"); // session reused for next turn
        _tracker.IncrementDenialBySessionId("sess", "x"); // → t2 only

        var snap = _tracker.Snapshot();
        Assert.Equal(1, snap.Single(s => s.TurnId == "t1").DenialCount);
        Assert.Equal(1, snap.Single(s => s.TurnId == "t2").DenialCount);
    }

    [Fact]
    public void UnlinkSession_StopsRouting()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        _tracker.LinkSession("sess", "t1");
        _tracker.UnlinkSession("sess");

        Assert.Equal(-1, _tracker.IncrementDenialBySessionId("sess", "x"));
        Assert.Equal(0, _tracker.Snapshot()[0].DenialCount);
    }

    [Fact]
    public void TryMarkStalledAndCancel_SignalsCts_AndReturnsFalseOnSecondCall()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);

        Assert.True(_tracker.TryMarkStalledAndCancel("t1", "test"));
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(TurnState.StallDetected, _tracker.Snapshot()[0].State);

        // Second call no-ops.
        Assert.False(_tracker.TryMarkStalledAndCancel("t1", "test-again"));
    }

    [Fact]
    public void TryMarkStalledAndCancel_DoesNotRemoveTurn()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        _tracker.TryMarkStalledAndCancel("t1", "test");

        // Entry still present (registration dispose is the only remove path).
        Assert.Single(_tracker.Snapshot());
    }

    [Fact]
    public void TryMarkStalledAndCancel_UnknownTurn_ReturnsFalse()
    {
        Assert.False(_tracker.TryMarkStalledAndCancel("nope", "x"));
    }

    [Fact]
    public void TryMarkStalledAndCancel_DisposedCts_DoesNotThrow()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        cts.Dispose(); // race: runner disposed CTS just before watchdog fires

        var ex = Record.Exception(() => _tracker.TryMarkStalledAndCancel("t1", "test"));
        Assert.Null(ex);
    }

    [Fact]
    public void Snapshot_ReturnsIndependentList()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);
        var s1 = _tracker.Snapshot();

        using var reg2 = _tracker.RegisterTurn("t2", "b", "B", "r", null, new CancellationTokenSource());
        var s2 = _tracker.Snapshot();

        // s1 should not have grown.
        Assert.Single(s1);
        Assert.Equal(2, s2.Count);
    }

    [Fact]
    public void LinkSession_NullOrEmpty_IsNoOp()
    {
        var cts = new CancellationTokenSource();
        using var reg = _tracker.RegisterTurn("t1", "a", "A", "r", null, cts);

        _tracker.LinkSession("", "t1");
        _tracker.UnlinkSession("");
        Assert.Equal(-1, _tracker.IncrementDenialBySessionId("", "x"));
    }
}
