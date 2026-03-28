using AgentAcademy.Server.Hubs;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentAcademy.Server.Tests;

public class ActivityHubBroadcasterTests
{
    private readonly ActivityBroadcaster _broadcaster;
    private readonly IHubContext<ActivityHub> _hubContext;
    private readonly IHubClients _hubClients;
    private readonly IClientProxy _clientProxy;
    private readonly ILogger<ActivityHubBroadcaster> _logger;
    private readonly ActivityHubBroadcaster _sut;

    public ActivityHubBroadcasterTests()
    {
        _broadcaster = new ActivityBroadcaster();
        _hubContext = Substitute.For<IHubContext<ActivityHub>>();
        _hubClients = Substitute.For<IHubClients>();
        _clientProxy = Substitute.For<IClientProxy>();
        _logger = Substitute.For<ILogger<ActivityHubBroadcaster>>();

        _hubContext.Clients.Returns(_hubClients);
        _hubClients.All.Returns(_clientProxy);

        _sut = new ActivityHubBroadcaster(_broadcaster, _hubContext, _logger);
    }

    #region StartAsync / StopAsync

    [Fact]
    public async Task StartAsync_SubscribesToBroadcaster()
    {
        await _sut.StartAsync(CancellationToken.None);

        // Verify subscription by broadcasting an event and checking it reaches the hub
        var evt = CreateEvent(ActivityEventType.RoomCreated, "Room created");
        _broadcaster.Broadcast(evt);

        // Allow fire-and-forget to complete
        await Task.Delay(100);

        await _clientProxy.Received(1).SendCoreAsync(
            "activityEvent",
            Arg.Is<object?[]>(args => args.Length == 1 && Equals(args[0], evt)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromBroadcaster()
    {
        await _sut.StartAsync(CancellationToken.None);
        await _sut.StopAsync(CancellationToken.None);

        // After stop, events should NOT reach the hub
        var evt = CreateEvent(ActivityEventType.RoomCreated, "Room created");
        _broadcaster.Broadcast(evt);

        await Task.Delay(100);

        await _clientProxy.DidNotReceive().SendCoreAsync(
            Arg.Any<string>(),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_CompletesImmediately()
    {
        var task = _sut.StartAsync(CancellationToken.None);
        Assert.True(task.IsCompleted);
        await task;
    }

    [Fact]
    public async Task StopAsync_CompletesImmediately()
    {
        await _sut.StartAsync(CancellationToken.None);
        var task = _sut.StopAsync(CancellationToken.None);
        Assert.True(task.IsCompleted);
        await task;
    }

    [Fact]
    public async Task StopAsync_IsIdempotent()
    {
        await _sut.StartAsync(CancellationToken.None);
        await _sut.StopAsync(CancellationToken.None);
        await _sut.StopAsync(CancellationToken.None); // Should not throw
    }

    #endregion

    #region Event Broadcasting

    [Fact]
    public async Task Broadcast_ForwardsAllEventTypes()
    {
        await _sut.StartAsync(CancellationToken.None);

        var events = new[]
        {
            CreateEvent(ActivityEventType.AgentLoaded, "Agent loaded"),
            CreateEvent(ActivityEventType.TaskCreated, "Task created"),
            CreateEvent(ActivityEventType.MessagePosted, "Message posted"),
        };

        foreach (var evt in events)
            _broadcaster.Broadcast(evt);

        await Task.Delay(200);

        await _clientProxy.Received(3).SendCoreAsync(
            "activityEvent",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Broadcast_SendsCorrectMethodName()
    {
        await _sut.StartAsync(CancellationToken.None);

        _broadcaster.Broadcast(CreateEvent(ActivityEventType.PhaseChanged, "Phase changed"));
        await Task.Delay(100);

        await _clientProxy.Received(1).SendCoreAsync(
            "activityEvent",
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Broadcast_PassesEventAsPayload()
    {
        await _sut.StartAsync(CancellationToken.None);

        var evt = CreateEvent(ActivityEventType.AgentFinished, "Agent finished");
        _broadcaster.Broadcast(evt);
        await Task.Delay(100);

        await _clientProxy.Received(1).SendCoreAsync(
            "activityEvent",
            Arg.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], evt)),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task Broadcast_ContinuesAfterSignalRError()
    {
        _clientProxy.SendCoreAsync(
            Arg.Any<string>(),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        await _sut.StartAsync(CancellationToken.None);

        // First event fails
        _broadcaster.Broadcast(CreateEvent(ActivityEventType.RoomCreated, "Event 1"));
        await Task.Delay(100);

        // Reset mock to succeed
        _clientProxy.SendCoreAsync(
            Arg.Any<string>(),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Second event should still be attempted
        _broadcaster.Broadcast(CreateEvent(ActivityEventType.RoomClosed, "Event 2"));
        await Task.Delay(100);

        // Should have received 2 calls total (one failed, one succeeded)
        await _clientProxy.Received(2).SendCoreAsync(
            Arg.Any<string>(),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Broadcast_DoesNotPropagateErrorToBroadcaster()
    {
        _clientProxy.SendCoreAsync(
            Arg.Any<string>(),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Boom"));

        await _sut.StartAsync(CancellationToken.None);

        // This should NOT throw — the error is swallowed inside the broadcaster
        _broadcaster.Broadcast(CreateEvent(ActivityEventType.AgentErrorOccurred, "Error"));
        await Task.Delay(100);
    }

    #endregion

    #region Helpers

    private static ActivityEvent CreateEvent(ActivityEventType type, string message)
    {
        return new ActivityEvent(
            Id: Guid.NewGuid().ToString(),
            Type: type,
            Severity: ActivitySeverity.Info,
            RoomId: "room-1",
            ActorId: "agent-1",
            TaskId: null,
            Message: message,
            CorrelationId: null,
            OccurredAt: DateTime.UtcNow);
    }

    #endregion
}
