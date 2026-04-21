using AgentAcademy.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AgentAcademy.Server.Hubs;

/// <summary>
/// SignalR hub for real-time activity event streaming.
/// Clients connect here to receive live updates from <see cref="Services.ActivityBroadcaster"/>.
/// The hub itself is thin — broadcasting is handled by <see cref="ActivityHubBroadcaster"/>.
/// </summary>
[Authorize]
public class ActivityHub : Hub
{
    private readonly SignalRConnectionTracker _tracker;

    public ActivityHub(SignalRConnectionTracker tracker)
    {
        _tracker = tracker;
    }

    public override async Task OnConnectedAsync()
    {
        _tracker.OnConnected(Context.ConnectionId, Context.UserIdentifier);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _tracker.OnDisconnected(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
