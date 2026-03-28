using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Hosted service that bridges <see cref="ActivityBroadcaster"/> events
/// to notification providers via <see cref="NotificationManager"/>.
/// Only forwards events that are meaningful as external notifications;
/// noisy/high-frequency events are filtered out.
/// </summary>
public sealed class ActivityNotificationBroadcaster : IHostedService
{
    private readonly ActivityBroadcaster _broadcaster;
    private readonly NotificationManager _notificationManager;
    private readonly ILogger<ActivityNotificationBroadcaster> _logger;
    private Action? _unsubscribe;

    /// <summary>
    /// Event types that trigger notifications. Everything else is filtered out.
    /// </summary>
    private static readonly HashSet<ActivityEventType> NotifiableEvents = new()
    {
        ActivityEventType.MessagePosted,
        ActivityEventType.TaskCreated,
        ActivityEventType.AgentErrorOccurred,
        ActivityEventType.AgentWarningOccurred,
        ActivityEventType.CommandExecuted,
        ActivityEventType.CommandDenied,
        ActivityEventType.CommandFailed,
    };

    public ActivityNotificationBroadcaster(
        ActivityBroadcaster broadcaster,
        NotificationManager notificationManager,
        ILogger<ActivityNotificationBroadcaster> logger)
    {
        _broadcaster = broadcaster;
        _notificationManager = notificationManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _unsubscribe = _broadcaster.Subscribe(OnActivityEvent);
        _logger.LogInformation("ActivityNotificationBroadcaster started — forwarding events to notification providers");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _unsubscribe?.Invoke();
        _unsubscribe = null;
        _logger.LogInformation("ActivityNotificationBroadcaster stopped");
        return Task.CompletedTask;
    }

    private void OnActivityEvent(ActivityEvent evt)
    {
        if (!NotifiableEvents.Contains(evt.Type))
            return;

        _ = SendNotificationAsync(evt);
    }

    private async Task SendNotificationAsync(ActivityEvent evt)
    {
        try
        {
            var message = MapToNotification(evt);
            if (message is null)
                return;

            await _notificationManager.SendToAllAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification for activity event {EventType}", evt.Type);
        }
    }

    /// <summary>
    /// Maps an <see cref="ActivityEvent"/> to a <see cref="NotificationMessage"/>.
    /// Returns null if the event should not produce a notification.
    /// </summary>
    internal static NotificationMessage? MapToNotification(ActivityEvent evt)
    {
        var mapped = evt.Type switch
        {
            ActivityEventType.MessagePosted => (
                Type: NotificationType.NeedsInput,
                Title: $"New message{FormatRoom(evt.RoomId)}"
            ),
            ActivityEventType.TaskCreated => (
                Type: NotificationType.TaskComplete,
                Title: $"Task created{FormatActor(evt.ActorId)}"
            ),
            ActivityEventType.AgentErrorOccurred => (
                Type: NotificationType.Error,
                Title: $"Agent error{FormatActor(evt.ActorId)}"
            ),
            ActivityEventType.AgentWarningOccurred => (
                Type: NotificationType.Error,
                Title: $"Agent warning{FormatActor(evt.ActorId)}"
            ),
            ActivityEventType.CommandExecuted => (
                Type: NotificationType.TaskComplete,
                Title: $"Command executed{FormatActor(evt.ActorId)}"
            ),
            ActivityEventType.CommandDenied => (
                Type: NotificationType.Error,
                Title: $"Command denied{FormatActor(evt.ActorId)}"
            ),
            ActivityEventType.CommandFailed => (
                Type: NotificationType.Error,
                Title: $"Command failed{FormatActor(evt.ActorId)}"
            ),
            _ => ((NotificationType Type, string Title)?)null
        };

        if (mapped is null)
            return null;

        return new NotificationMessage(
            Type: mapped.Value.Type,
            Title: mapped.Value.Title,
            Body: evt.Message,
            RoomId: evt.RoomId,
            AgentName: evt.ActorId
        );
    }

    private static string FormatRoom(string? roomId) =>
        string.IsNullOrEmpty(roomId) ? "" : $" in {roomId}";

    private static string FormatActor(string? actorId) =>
        string.IsNullOrEmpty(actorId) ? "" : $": {actorId}";
}
