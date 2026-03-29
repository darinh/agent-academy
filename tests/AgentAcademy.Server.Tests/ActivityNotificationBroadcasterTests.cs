using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class ActivityNotificationBroadcasterTests
{
    private readonly ActivityBroadcaster _broadcaster;
    private readonly NotificationManager _notificationManager;
    private readonly ILogger<ActivityNotificationBroadcaster> _logger;
    private readonly ActivityNotificationBroadcaster _sut;

    public ActivityNotificationBroadcasterTests()
    {
        _broadcaster = new ActivityBroadcaster();
        _notificationManager = new NotificationManager(
            Substitute.For<ILogger<NotificationManager>>());
        _logger = Substitute.For<ILogger<ActivityNotificationBroadcaster>>();
        _sut = new ActivityNotificationBroadcaster(_broadcaster, _notificationManager, _logger);
    }

    #region StartAsync / StopAsync

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
        await _sut.StopAsync(CancellationToken.None);
    }

    #endregion

    #region Event Mapping

    [Theory]
    [InlineData(ActivityEventType.MessagePosted, NotificationType.NeedsInput)]
    [InlineData(ActivityEventType.TaskCreated, NotificationType.TaskComplete)]
    [InlineData(ActivityEventType.AgentErrorOccurred, NotificationType.Error)]
    [InlineData(ActivityEventType.AgentWarningOccurred, NotificationType.Error)]
    [InlineData(ActivityEventType.CommandExecuted, NotificationType.TaskComplete)]
    [InlineData(ActivityEventType.CommandDenied, NotificationType.Error)]
    [InlineData(ActivityEventType.CommandFailed, NotificationType.Error)]
    public void MapToNotification_MapsNotifiableEvents(ActivityEventType eventType, NotificationType expectedNotifType)
    {
        var evt = CreateEvent(eventType, "Test message");
        var result = ActivityNotificationBroadcaster.MapToNotification(evt);

        Assert.NotNull(result);
        Assert.Equal(expectedNotifType, result.Type);
        Assert.Equal("Test message", result.Body);
        Assert.Equal("room-1", result.RoomId);
        Assert.Equal("agent-1", result.AgentName);
    }

    [Theory]
    [InlineData(ActivityEventType.AgentThinking)]
    [InlineData(ActivityEventType.AgentFinished)]
    [InlineData(ActivityEventType.AgentLoaded)]
    [InlineData(ActivityEventType.RoomCreated)]
    [InlineData(ActivityEventType.RoomClosed)]
    [InlineData(ActivityEventType.PhaseChanged)]
    [InlineData(ActivityEventType.PresenceUpdated)]
    [InlineData(ActivityEventType.RoomStatusChanged)]
    [InlineData(ActivityEventType.MessageSent)]
    [InlineData(ActivityEventType.SubagentStarted)]
    [InlineData(ActivityEventType.SubagentCompleted)]
    [InlineData(ActivityEventType.SubagentFailed)]
    public void MapToNotification_ReturnsNullForNonNotifiableEvents(ActivityEventType eventType)
    {
        var evt = CreateEvent(eventType, "Test message");
        var result = ActivityNotificationBroadcaster.MapToNotification(evt);

        Assert.Null(result);
    }

    [Fact]
    public void MapToNotification_IncludesRoomInTitleForMessagePosted()
    {
        var evt = CreateEvent(ActivityEventType.MessagePosted, "Hello", roomId: "lobby");
        var result = ActivityNotificationBroadcaster.MapToNotification(evt);

        Assert.NotNull(result);
        Assert.Contains("lobby", result.Title);
    }

    [Fact]
    public void MapToNotification_IncludesActorInTitleForErrors()
    {
        var evt = CreateEvent(ActivityEventType.AgentErrorOccurred, "Crash", actorId: "architect");
        var result = ActivityNotificationBroadcaster.MapToNotification(evt);

        Assert.NotNull(result);
        Assert.Contains("architect", result.Title);
    }

    [Fact]
    public void MapToNotification_HandlesNullRoomId()
    {
        var evt = CreateEvent(ActivityEventType.MessagePosted, "Hello", roomId: null);
        var result = ActivityNotificationBroadcaster.MapToNotification(evt);

        Assert.NotNull(result);
        Assert.DoesNotContain(" in ", result.Title);
        Assert.Null(result.RoomId);
    }

    [Fact]
    public void MapToNotification_HandlesNullActorId()
    {
        var evt = CreateEvent(ActivityEventType.AgentErrorOccurred, "Error", actorId: null);
        var result = ActivityNotificationBroadcaster.MapToNotification(evt);

        Assert.NotNull(result);
        Assert.DoesNotContain(":", result.Title);
        Assert.Null(result.AgentName);
    }

    #endregion

    #region Integration (subscribe + broadcast)

    [Fact]
    public async Task NotifiableEvent_IsForwardedToManager()
    {
        var received = new List<NotificationMessage>();
        var provider = new TestNotificationProvider(received);
        _notificationManager.RegisterProvider(provider);
        provider.SetConnected(true);

        await _sut.StartAsync(CancellationToken.None);

        _broadcaster.Broadcast(CreateEvent(ActivityEventType.TaskCreated, "New task"));
        await Task.Delay(200);

        Assert.Single(received);
        Assert.Equal(NotificationType.TaskComplete, received[0].Type);
        Assert.Equal("New task", received[0].Body);
    }

    [Fact]
    public async Task NonNotifiableEvent_IsNotForwarded()
    {
        var received = new List<NotificationMessage>();
        var provider = new TestNotificationProvider(received);
        _notificationManager.RegisterProvider(provider);
        provider.SetConnected(true);

        await _sut.StartAsync(CancellationToken.None);

        _broadcaster.Broadcast(CreateEvent(ActivityEventType.AgentThinking, "Thinking"));
        await Task.Delay(200);

        Assert.Empty(received);
    }

    [Fact]
    public async Task HumanMessagePosted_IsNotForwarded()
    {
        var received = new List<NotificationMessage>();
        var provider = new TestNotificationProvider(received);
        _notificationManager.RegisterProvider(provider);
        provider.SetConnected(true);

        await _sut.StartAsync(CancellationToken.None);

        // Human-originated MessagePosted should be suppressed
        _broadcaster.Broadcast(CreateEvent(ActivityEventType.MessagePosted, "You: hello", actorId: "human"));
        await Task.Delay(200);

        Assert.Empty(received);
    }

    [Fact]
    public async Task AgentMessagePosted_IsForwarded()
    {
        var received = new List<NotificationMessage>();
        var provider = new TestNotificationProvider(received);
        _notificationManager.RegisterProvider(provider);
        provider.SetConnected(true);

        await _sut.StartAsync(CancellationToken.None);

        // Agent-originated MessagePosted should still be forwarded
        _broadcaster.Broadcast(CreateEvent(ActivityEventType.MessagePosted, "Agent response", actorId: "planner-1"));
        await Task.Delay(200);

        Assert.Single(received);
        Assert.Equal("Agent response", received[0].Body);
    }

    [Fact]
    public async Task StopAsync_PreventsForwarding()
    {
        var received = new List<NotificationMessage>();
        var provider = new TestNotificationProvider(received);
        _notificationManager.RegisterProvider(provider);
        provider.SetConnected(true);

        await _sut.StartAsync(CancellationToken.None);
        await _sut.StopAsync(CancellationToken.None);

        _broadcaster.Broadcast(CreateEvent(ActivityEventType.TaskCreated, "New task"));
        await Task.Delay(200);

        Assert.Empty(received);
    }

    [Fact]
    public async Task ProviderException_DoesNotCrashBroadcaster()
    {
        var provider = new ThrowingNotificationProvider();
        _notificationManager.RegisterProvider(provider);

        await _sut.StartAsync(CancellationToken.None);

        // Should not throw
        _broadcaster.Broadcast(CreateEvent(ActivityEventType.AgentErrorOccurred, "Error"));
        await Task.Delay(200);

        // Verify the broadcaster is still subscribed by sending another event
        _broadcaster.Broadcast(CreateEvent(ActivityEventType.TaskCreated, "Task"));
        await Task.Delay(200);
    }

    #endregion

    #region Helpers

    private static ActivityEvent CreateEvent(
        ActivityEventType type,
        string message,
        string? roomId = "room-1",
        string? actorId = "agent-1")
    {
        return new ActivityEvent(
            Id: Guid.NewGuid().ToString(),
            Type: type,
            Severity: ActivitySeverity.Info,
            RoomId: roomId,
            ActorId: actorId,
            TaskId: null,
            Message: message,
            CorrelationId: null,
            OccurredAt: DateTime.UtcNow);
    }

    /// <summary>
    /// Test notification provider that records received messages.
    /// </summary>
    private sealed class TestNotificationProvider : INotificationProvider
    {
        private readonly List<NotificationMessage> _received;
        private bool _connected;

        public TestNotificationProvider(List<NotificationMessage> received) => _received = received;

        public string ProviderId => "test";
        public string DisplayName => "Test";
        public bool IsConfigured => true;
        public bool IsConnected => _connected;

        public void SetConnected(bool connected) => _connected = connected;

        public Task ConfigureAsync(Dictionary<string, string> configuration, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ConnectAsync(CancellationToken ct = default) { _connected = true; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken ct = default) { _connected = false; return Task.CompletedTask; }

        public Task<bool> SendNotificationAsync(NotificationMessage message, CancellationToken ct = default)
        {
            _received.Add(message);
            return Task.FromResult(true);
        }

        public Task<UserResponse?> RequestInputAsync(InputRequest request, CancellationToken ct = default)
            => Task.FromResult<UserResponse?>(null);

        public ProviderConfigSchema GetConfigSchema()
            => new("test", "Test", "Test provider", []);
    }

    /// <summary>
    /// Provider that always throws to test error handling.
    /// </summary>
    private sealed class ThrowingNotificationProvider : INotificationProvider
    {
        public string ProviderId => "throwing";
        public string DisplayName => "Throwing";
        public bool IsConfigured => true;
        public bool IsConnected => true;

        public Task ConfigureAsync(Dictionary<string, string> configuration, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> SendNotificationAsync(NotificationMessage message, CancellationToken ct = default)
            => throw new InvalidOperationException("Provider exploded");

        public Task<UserResponse?> RequestInputAsync(InputRequest request, CancellationToken ct = default)
            => Task.FromResult<UserResponse?>(null);

        public ProviderConfigSchema GetConfigSchema()
            => new("throwing", "Throwing", "Throws on send", []);
    }

    #endregion

    // ── ExtractNewNameFromDetail ────────────────────────────────

    [Theory]
    [InlineData("Room renamed: \"Old Name\" → \"New Name\"", "New Name")]
    [InlineData("Room renamed: \"A\" → \"B\"", "B")]
    [InlineData(null, null)]
    [InlineData("No arrow here", null)]
    [InlineData("Room renamed: \"Old\" → ", null)]
    public void ExtractNewNameFromDetail_ParsesCorrectly(string? detail, string? expected)
    {
        var result = ActivityNotificationBroadcaster.ExtractNewNameFromDetail(detail);
        Assert.Equal(expected, result);
    }
}
