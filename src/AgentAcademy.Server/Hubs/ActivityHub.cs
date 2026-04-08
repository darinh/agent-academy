using Microsoft.AspNetCore.SignalR;

namespace AgentAcademy.Server.Hubs;

/// <summary>
/// SignalR hub for real-time activity event streaming.
/// Clients connect here to receive live updates from <see cref="Services.ActivityBroadcaster"/>.
/// The hub itself is thin — broadcasting is handled by <see cref="ActivityHubBroadcaster"/>.
/// </summary>
public class ActivityHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}
