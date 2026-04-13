using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public sealed class MessageBroadcasterTests
{
    private static ChatEnvelope MakeMessage(string roomId, string id = "msg-1", string content = "hello") =>
        new(id, roomId, "agent-1", "Agent 1", "Engineer", MessageSenderKind.Agent,
            MessageKind.Response, content, DateTime.UtcNow);

    // ── Subscribe & Broadcast ───────────────────────────────────

    [Fact]
    public void Broadcast_NoSubscribers_DoesNotThrow()
    {
        var sut = new MessageBroadcaster();
        sut.Broadcast("room-1", MakeMessage("room-1"));
    }

    [Fact]
    public void Broadcast_DeliversToSubscriber()
    {
        var sut = new MessageBroadcaster();
        ChatEnvelope? received = null;
        sut.Subscribe("room-1", msg => received = msg);

        var sent = MakeMessage("room-1");
        sut.Broadcast("room-1", sent);

        Assert.NotNull(received);
        Assert.Equal(sent.Id, received.Id);
    }

    [Fact]
    public void Broadcast_DeliversToMultipleSubscribers()
    {
        var sut = new MessageBroadcaster();
        var received = new List<ChatEnvelope>();
        sut.Subscribe("room-1", msg => received.Add(msg));
        sut.Subscribe("room-1", msg => received.Add(msg));

        sut.Broadcast("room-1", MakeMessage("room-1"));

        Assert.Equal(2, received.Count);
    }

    [Fact]
    public void Broadcast_IsolatesRooms()
    {
        var sut = new MessageBroadcaster();
        ChatEnvelope? room1Msg = null;
        ChatEnvelope? room2Msg = null;
        sut.Subscribe("room-1", msg => room1Msg = msg);
        sut.Subscribe("room-2", msg => room2Msg = msg);

        sut.Broadcast("room-1", MakeMessage("room-1"));

        Assert.NotNull(room1Msg);
        Assert.Null(room2Msg);
    }

    [Fact]
    public void Broadcast_DifferentRoomMessages_GoToCorrectSubscribers()
    {
        var sut = new MessageBroadcaster();
        var room1Messages = new List<string>();
        var room2Messages = new List<string>();
        sut.Subscribe("room-1", msg => room1Messages.Add(msg.Id));
        sut.Subscribe("room-2", msg => room2Messages.Add(msg.Id));

        sut.Broadcast("room-1", MakeMessage("room-1", "a"));
        sut.Broadcast("room-2", MakeMessage("room-2", "b"));
        sut.Broadcast("room-1", MakeMessage("room-1", "c"));

        Assert.Equal(["a", "c"], room1Messages);
        Assert.Equal(["b"], room2Messages);
    }

    // ── Unsubscribe ─────────────────────────────────────────────

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var sut = new MessageBroadcaster();
        var received = new List<string>();
        var unsub = sut.Subscribe("room-1", msg => received.Add(msg.Id));

        sut.Broadcast("room-1", MakeMessage("room-1", "before"));
        unsub();
        sut.Broadcast("room-1", MakeMessage("room-1", "after"));

        Assert.Single(received);
        Assert.Equal("before", received[0]);
    }

    [Fact]
    public void Unsubscribe_OnlyRemovesTargetSubscriber()
    {
        var sut = new MessageBroadcaster();
        var received1 = new List<string>();
        var received2 = new List<string>();
        var unsub1 = sut.Subscribe("room-1", msg => received1.Add(msg.Id));
        sut.Subscribe("room-1", msg => received2.Add(msg.Id));

        unsub1();
        sut.Broadcast("room-1", MakeMessage("room-1"));

        Assert.Empty(received1);
        Assert.Single(received2);
    }

    [Fact]
    public void Unsubscribe_CalledTwice_DoesNotThrow()
    {
        var sut = new MessageBroadcaster();
        var unsub = sut.Subscribe("room-1", _ => { });
        unsub();
        unsub(); // idempotent
    }

    [Fact]
    public void Unsubscribe_LastSubscriber_CleansUpRoom()
    {
        var sut = new MessageBroadcaster();
        var unsub = sut.Subscribe("room-1", _ => { });
        Assert.Equal(1, sut.GetSubscriberCount("room-1"));

        unsub();
        Assert.Equal(0, sut.GetSubscriberCount("room-1"));
    }

    // ── GetSubscriberCount ──────────────────────────────────────

    [Fact]
    public void GetSubscriberCount_ReturnsZeroForUnknownRoom()
    {
        var sut = new MessageBroadcaster();
        Assert.Equal(0, sut.GetSubscriberCount("nonexistent"));
    }

    [Fact]
    public void GetSubscriberCount_TracksMultipleSubscribers()
    {
        var sut = new MessageBroadcaster();
        sut.Subscribe("room-1", _ => { });
        sut.Subscribe("room-1", _ => { });
        sut.Subscribe("room-2", _ => { });

        Assert.Equal(2, sut.GetSubscriberCount("room-1"));
        Assert.Equal(1, sut.GetSubscriberCount("room-2"));
    }

    // ── Error Resilience ────────────────────────────────────────

    [Fact]
    public void Broadcast_SubscriberThrows_OtherSubscribersStillReceive()
    {
        var sut = new MessageBroadcaster();
        ChatEnvelope? received = null;
        sut.Subscribe("room-1", _ => throw new InvalidOperationException("boom"));
        sut.Subscribe("room-1", msg => received = msg);

        sut.Broadcast("room-1", MakeMessage("room-1"));

        Assert.NotNull(received);
    }

    // ── Thread Safety ───────────────────────────────────────────

    [Fact]
    public async Task ConcurrentBroadcastAndSubscribe_DoesNotThrow()
    {
        var sut = new MessageBroadcaster();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var messageCount = 0;

        var subscriberTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var unsub = sut.Subscribe("room-1", _ => Interlocked.Increment(ref messageCount));
                await Task.Delay(1, CancellationToken.None);
                unsub();
            }
        });

        var broadcastTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                sut.Broadcast("room-1", MakeMessage("room-1", Guid.NewGuid().ToString("N")));
                await Task.Delay(1, CancellationToken.None);
            }
        });

        await Task.WhenAll(subscriberTask, broadcastTask);
        Assert.True(messageCount > 0);
    }
}
