using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public class ActivityBroadcasterTests
{
    private readonly ActivityBroadcaster _sut = new();

    private static ActivityEvent MakeEvent(string id, string message = "test")
    {
        return new ActivityEvent(
            Id: id,
            Type: ActivityEventType.RoomCreated,
            Severity: ActivitySeverity.Info,
            RoomId: "room-1",
            ActorId: "agent-1",
            TaskId: null,
            Message: message,
            CorrelationId: null,
            OccurredAt: DateTime.UtcNow);
    }

    #region Broadcast — buffer behaviour

    [Fact]
    public void Broadcast_SingleEvent_StoresInBuffer()
    {
        _sut.Broadcast(MakeEvent("e1"));

        var recent = _sut.GetRecentActivity();
        Assert.Single(recent);
        Assert.Equal("e1", recent[0].Id);
    }

    [Fact]
    public void Broadcast_MultipleEvents_StoresAll()
    {
        _sut.Broadcast(MakeEvent("e1"));
        _sut.Broadcast(MakeEvent("e2"));
        _sut.Broadcast(MakeEvent("e3"));

        var recent = _sut.GetRecentActivity();
        Assert.Equal(3, recent.Count);
        Assert.Equal("e1", recent[0].Id);
        Assert.Equal("e2", recent[1].Id);
        Assert.Equal("e3", recent[2].Id);
    }

    [Fact]
    public void Broadcast_ExceedsMaxBuffer_TrimsOldestEvents()
    {
        for (var i = 0; i < 105; i++)
            _sut.Broadcast(MakeEvent($"e{i}"));

        var recent = _sut.GetRecentActivity();
        Assert.Equal(100, recent.Count);
        // Oldest 5 should have been trimmed; first remaining is e5
        Assert.Equal("e5", recent[0].Id);
        Assert.Equal("e104", recent[99].Id);
    }

    [Fact]
    public void Broadcast_ExactlyMaxBuffer_DoesNotTrim()
    {
        for (var i = 0; i < 100; i++)
            _sut.Broadcast(MakeEvent($"e{i}"));

        var recent = _sut.GetRecentActivity();
        Assert.Equal(100, recent.Count);
        Assert.Equal("e0", recent[0].Id);
        Assert.Equal("e99", recent[99].Id);
    }

    [Fact]
    public void Broadcast_MaxBufferPlusOne_TrimsToMaxBuffer()
    {
        for (var i = 0; i < 101; i++)
            _sut.Broadcast(MakeEvent($"e{i}"));

        var recent = _sut.GetRecentActivity();
        Assert.Equal(100, recent.Count);
        Assert.Equal("e1", recent[0].Id);
        Assert.Equal("e100", recent[99].Id);
    }

    [Fact]
    public void Broadcast_NoSubscribers_StillBuffers()
    {
        // No Subscribe() calls — broadcasting should still buffer events
        _sut.Broadcast(MakeEvent("solo"));

        var recent = _sut.GetRecentActivity();
        Assert.Single(recent);
        Assert.Equal("solo", recent[0].Id);
    }

    #endregion

    #region Broadcast — subscriber notification

    [Fact]
    public void Broadcast_NotifiesSubscribers()
    {
        ActivityEvent? received = null;
        _sut.Subscribe(e => received = e);

        var evt = MakeEvent("n1");
        _sut.Broadcast(evt);

        Assert.NotNull(received);
        Assert.Equal("n1", received!.Id);
    }

    [Fact]
    public void Broadcast_NotifiesMultipleSubscribers()
    {
        var received1 = new List<ActivityEvent>();
        var received2 = new List<ActivityEvent>();
        _sut.Subscribe(e => received1.Add(e));
        _sut.Subscribe(e => received2.Add(e));

        _sut.Broadcast(MakeEvent("m1"));

        Assert.Single(received1);
        Assert.Single(received2);
        Assert.Equal("m1", received1[0].Id);
        Assert.Equal("m1", received2[0].Id);
    }

    [Fact]
    public void Broadcast_SubscriberThrows_DoesNotAffectOtherSubscribers()
    {
        ActivityEvent? received = null;
        _sut.Subscribe(_ => throw new InvalidOperationException("boom"));
        _sut.Subscribe(e => received = e);

        _sut.Broadcast(MakeEvent("err1"));

        Assert.NotNull(received);
        Assert.Equal("err1", received!.Id);
    }

    [Fact]
    public void Broadcast_SubscriberThrows_DoesNotAffectBuffer()
    {
        _sut.Subscribe(_ => throw new InvalidOperationException("boom"));

        _sut.Broadcast(MakeEvent("err2"));

        var recent = _sut.GetRecentActivity();
        Assert.Single(recent);
        Assert.Equal("err2", recent[0].Id);
    }

    #endregion

    #region GetRecentActivity

    [Fact]
    public void GetRecentActivity_EmptyBuffer_ReturnsEmptyList()
    {
        var recent = _sut.GetRecentActivity();
        Assert.Empty(recent);
    }

    [Fact]
    public void GetRecentActivity_ReturnsCopyNotReference()
    {
        _sut.Broadcast(MakeEvent("c1"));

        var first = _sut.GetRecentActivity();
        var second = _sut.GetRecentActivity();

        Assert.NotSame(first, second);
        Assert.Equal(first.Count, second.Count);

        // Mutating the returned list should not affect subsequent calls
        _sut.Broadcast(MakeEvent("c2"));
        var third = _sut.GetRecentActivity();
        Assert.Equal(2, third.Count);
        Assert.Single(first); // original snapshot unchanged
    }

    [Fact]
    public void GetRecentActivity_AfterBroadcast_ContainsEvent()
    {
        var evt = MakeEvent("ab1", "after-broadcast");
        _sut.Broadcast(evt);

        var recent = _sut.GetRecentActivity();
        Assert.Contains(recent, e => e.Id == "ab1" && e.Message == "after-broadcast");
    }

    #endregion

    #region Subscribe / Unsubscribe

    [Fact]
    public void Subscribe_ReturnsUnsubscribeAction()
    {
        var unsubscribe = _sut.Subscribe(_ => { });
        Assert.NotNull(unsubscribe);
    }

    [Fact]
    public void Subscribe_Unsubscribe_NoLongerNotified()
    {
        var count = 0;
        var unsubscribe = _sut.Subscribe(_ => count++);

        _sut.Broadcast(MakeEvent("s1"));
        Assert.Equal(1, count);

        unsubscribe();

        _sut.Broadcast(MakeEvent("s2"));
        Assert.Equal(1, count); // no further notifications
    }

    [Fact]
    public void Subscribe_MultipleSubscribers_AllNotified()
    {
        var counts = new int[3];
        _sut.Subscribe(_ => counts[0]++);
        _sut.Subscribe(_ => counts[1]++);
        _sut.Subscribe(_ => counts[2]++);

        _sut.Broadcast(MakeEvent("ms1"));

        Assert.All(counts, c => Assert.Equal(1, c));
    }

    [Fact]
    public void Subscribe_UnsubscribeOne_OthersStillNotified()
    {
        var countA = 0;
        var countB = 0;
        var unsubA = _sut.Subscribe(_ => countA++);
        _sut.Subscribe(_ => countB++);

        _sut.Broadcast(MakeEvent("u1"));
        Assert.Equal(1, countA);
        Assert.Equal(1, countB);

        unsubA();

        _sut.Broadcast(MakeEvent("u2"));
        Assert.Equal(1, countA); // unsubscribed
        Assert.Equal(2, countB); // still active
    }

    #endregion

    #region Concurrency

    [Fact]
    public void ConcurrentBroadcast_DoesNotThrow()
    {
        var events = Enumerable.Range(0, 500)
            .Select(i => MakeEvent($"par-{i}"))
            .ToList();

        Parallel.ForEach(events, evt => _sut.Broadcast(evt));

        var recent = _sut.GetRecentActivity();
        // Buffer should be capped at 100
        Assert.Equal(100, recent.Count);
        // All IDs should be unique
        Assert.Equal(recent.Count, recent.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentSubscribeUnsubscribe_DoesNotThrow()
    {
        var tasks = new List<Task>();

        // Rapidly subscribe and unsubscribe from multiple threads
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var unsub = _sut.Subscribe(_ => { });
                _sut.Broadcast(MakeEvent($"conc-{Environment.CurrentManagedThreadId}"));
                unsub();
            }));
        }

        await Task.WhenAll(tasks);
    }

    #endregion
}
