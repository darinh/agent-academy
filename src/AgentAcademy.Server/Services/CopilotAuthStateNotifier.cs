using AgentAcademy.Server.Notifications.Contracts;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Maps Copilot auth-state transitions into room status messages + provider notifications.
/// </summary>
public sealed class CopilotAuthStateNotifier : ICopilotAuthStateNotifier
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INotificationManager _notificationManager;

    public CopilotAuthStateNotifier(
        IServiceScopeFactory scopeFactory,
        INotificationManager notificationManager)
    {
        _scopeFactory = scopeFactory;
        _notificationManager = notificationManager;
    }

    public async Task NotifyAsync(bool degraded, string roomId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        var roomMessage = degraded
            ? "⚠️ **Copilot SDK authentication failed.** The OAuth token has expired or been revoked. Please re-authenticate at `/api/auth/login` to restore agent functionality."
            : "✅ **Copilot SDK reconnected.** A new token has been provided — agents are coming back online.";
        var notification = degraded
            ? new NotificationMessage(
                Type: NotificationType.Error,
                Title: "Copilot SDK authentication degraded",
                Body: "The GitHub auth probe received 401/403 from `GET /user`. Re-authenticate at `/api/auth/login` to restore agent functionality.",
                RoomId: roomId)
            : new NotificationMessage(
                Type: NotificationType.TaskComplete,
                Title: "Copilot SDK authentication restored",
                Body: "Copilot access is healthy again. Agents are coming back online.",
                RoomId: roomId);

        using var scope = _scopeFactory.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        await messageService.PostSystemStatusAsync(roomId, roomMessage);
        await _notificationManager.SendToAllAsync(notification, ct);
    }
}
