using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Singleton in-memory buffer and broadcaster for activity events.
/// Separated from scoped services so activity state
/// persists across HTTP requests and subscribers survive request lifetimes.
/// </summary>
public sealed class ActivityBroadcaster
{
    private readonly List<ActivityEvent> _recentActivity = [];
    private readonly List<Action<ActivityEvent>> _subscribers = [];
    private readonly object _lock = new();
    private const int MaxBufferSize = 100;

    /// <summary>
    /// Buffers an event and notifies all subscribers.
    /// Subscribers are called OUTSIDE the lock to prevent deadlocks.
    /// </summary>
    public void Broadcast(ActivityEvent evt)
    {
        List<Action<ActivityEvent>> snapshot;

        lock (_lock)
        {
            _recentActivity.Add(evt);
            if (_recentActivity.Count > MaxBufferSize)
                _recentActivity.RemoveAt(0);

            snapshot = [.. _subscribers];
        }

        // Invoke subscribers outside the lock to prevent deadlocks
        // (e.g., subscriber calling GetRecentActivity or Unsubscribe)
        foreach (var sub in snapshot)
        {
            try { sub(evt); }
            catch { /* subscriber errors are swallowed */ }
        }
    }

    /// <summary>
    /// Returns a copy of recent activity events.
    /// </summary>
    public IReadOnlyList<ActivityEvent> GetRecentActivity()
    {
        lock (_lock)
        {
            return [.. _recentActivity];
        }
    }

    /// <summary>
    /// Subscribes to activity events. Returns an unsubscribe action.
    /// </summary>
    public Action Subscribe(Action<ActivityEvent> callback)
    {
        lock (_lock)
        {
            _subscribers.Add(callback);
        }

        return () =>
        {
            lock (_lock)
            {
                _subscribers.Remove(callback);
            }
        };
    }
}
