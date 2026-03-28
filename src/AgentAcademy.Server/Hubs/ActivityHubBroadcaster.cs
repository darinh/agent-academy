using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Hubs;

/// <summary>
/// Hosted service that bridges <see cref="ActivityBroadcaster"/> events
/// to all connected SignalR clients via <see cref="ActivityHub"/>.
/// Subscribes on start, unsubscribes on stop.
/// </summary>
public sealed class ActivityHubBroadcaster : IHostedService
{
    private readonly ActivityBroadcaster _broadcaster;
    private readonly IHubContext<ActivityHub> _hubContext;
    private readonly ILogger<ActivityHubBroadcaster> _logger;
    private Action? _unsubscribe;

    public ActivityHubBroadcaster(
        ActivityBroadcaster broadcaster,
        IHubContext<ActivityHub> hubContext,
        ILogger<ActivityHubBroadcaster> logger)
    {
        _broadcaster = broadcaster;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _unsubscribe = _broadcaster.Subscribe(OnActivityEvent);
        _logger.LogInformation("ActivityHubBroadcaster started — forwarding events to SignalR clients");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _unsubscribe?.Invoke();
        _unsubscribe = null;
        _logger.LogInformation("ActivityHubBroadcaster stopped");
        return Task.CompletedTask;
    }

    private void OnActivityEvent(ActivityEvent evt)
    {
        // Fire-and-forget: SignalR broadcast should not block the ActivityBroadcaster.
        // Errors are logged but do not propagate to the broadcaster.
        _ = SendAsync(evt);
    }

    private async Task SendAsync(ActivityEvent evt)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("activityEvent", evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast activity event {EventType} via SignalR", evt.Type);
        }
    }
}
