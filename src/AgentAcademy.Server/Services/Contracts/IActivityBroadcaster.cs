using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Singleton in-memory buffer and broadcaster for activity events.
/// Activity state persists across HTTP requests and subscribers
/// survive request lifetimes.
/// </summary>
public interface IActivityBroadcaster
{
    /// <summary>
    /// Buffers an event and notifies all subscribers.
    /// </summary>
    void Broadcast(ActivityEvent evt);

    /// <summary>
    /// Returns a copy of recent activity events.
    /// </summary>
    IReadOnlyList<ActivityEvent> GetRecentActivity();

    /// <summary>
    /// Subscribes to activity events. Returns an unsubscribe action.
    /// </summary>
    Action Subscribe(Action<ActivityEvent> callback);
}
