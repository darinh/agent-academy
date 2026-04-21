using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Singleton in-memory broadcaster for room and DM messages.
/// Enables SSE streaming by allowing per-room and per-DM-thread subscriptions.
/// Unlike <see cref="ActivityBroadcaster"/>, this has no buffer —
/// the SSE endpoint handles replay from the database.
/// </summary>
public sealed class MessageBroadcaster : IMessageBroadcaster
{
    private readonly Dictionary<string, List<Action<ChatEnvelope>>> _roomSubscribers = new();
    private readonly Dictionary<string, List<Action<DmMessage>>> _dmSubscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action<string, DmMessage>> _globalDmSubscribers = [];
    private readonly object _lock = new();

    // ── Room subscriptions ──────────────────────────────────────

    /// <summary>
    /// Broadcasts a message to all subscribers of the given room.
    /// Subscribers are called OUTSIDE the lock to prevent deadlocks.
    /// </summary>
    public void Broadcast(string roomId, ChatEnvelope message)
    {
        List<Action<ChatEnvelope>> snapshot;

        lock (_lock)
        {
            if (!_roomSubscribers.TryGetValue(roomId, out var subs) || subs.Count == 0)
                return;
            snapshot = [.. subs];
        }

        foreach (var sub in snapshot)
        {
            try { sub(message); }
            catch { /* subscriber errors are swallowed */ }
        }
    }

    /// <summary>
    /// Subscribes to messages in a specific room. Returns an unsubscribe action.
    /// </summary>
    public Action Subscribe(string roomId, Action<ChatEnvelope> callback)
    {
        lock (_lock)
        {
            if (!_roomSubscribers.TryGetValue(roomId, out var subs))
            {
                subs = [];
                _roomSubscribers[roomId] = subs;
            }
            subs.Add(callback);
        }

        return () =>
        {
            lock (_lock)
            {
                if (_roomSubscribers.TryGetValue(roomId, out var subs))
                {
                    subs.Remove(callback);
                    if (subs.Count == 0)
                        _roomSubscribers.Remove(roomId);
                }
            }
        };
    }

    /// <summary>
    /// Returns the number of active subscribers for a room (for diagnostics).
    /// </summary>
    public int GetSubscriberCount(string roomId)
    {
        lock (_lock)
        {
            return _roomSubscribers.TryGetValue(roomId, out var subs) ? subs.Count : 0;
        }
    }

    // ── DM thread subscriptions ─────────────────────────────────

    /// <summary>
    /// Broadcasts a DM to all subscribers of the given agent's DM thread.
    /// The thread is identified by agentId — messages flow in both directions
    /// (human→agent and agent→human) on the same subscription.
    /// </summary>
    public void BroadcastDm(string agentId, DmMessage message)
    {
        List<Action<DmMessage>>? threadSnapshot;
        List<Action<string, DmMessage>>? globalSnapshot;

        lock (_lock)
        {
            threadSnapshot = _dmSubscribers.TryGetValue(agentId, out var subs) && subs.Count > 0
                ? [.. subs]
                : null;
            globalSnapshot = _globalDmSubscribers.Count > 0 ? [.. _globalDmSubscribers] : null;
        }

        if (threadSnapshot is not null)
        {
            foreach (var sub in threadSnapshot)
            {
                try { sub(message); }
                catch { /* subscriber errors are swallowed */ }
            }
        }

        if (globalSnapshot is not null)
        {
            foreach (var sub in globalSnapshot)
            {
                try { sub(agentId, message); }
                catch { /* subscriber errors are swallowed */ }
            }
        }
    }

    /// <summary>
    /// Subscribes to DM messages in a specific agent's thread. Returns an unsubscribe action.
    /// </summary>
    public Action SubscribeDm(string agentId, Action<DmMessage> callback)
    {
        lock (_lock)
        {
            if (!_dmSubscribers.TryGetValue(agentId, out var subs))
            {
                subs = [];
                _dmSubscribers[agentId] = subs;
            }
            subs.Add(callback);
        }

        return () =>
        {
            lock (_lock)
            {
                if (_dmSubscribers.TryGetValue(agentId, out var subs))
                {
                    subs.Remove(callback);
                    if (subs.Count == 0)
                        _dmSubscribers.Remove(agentId);
                }
            }
        };
    }

    /// <summary>
    /// Returns the number of active DM subscribers for an agent thread (for diagnostics).
    /// </summary>
    public int GetDmSubscriberCount(string agentId)
    {
        lock (_lock)
        {
            return _dmSubscribers.TryGetValue(agentId, out var subs) ? subs.Count : 0;
        }
    }

    // ── Global DM subscriptions ─────────────────────────────────

    /// <summary>
    /// Subscribes to ALL DM messages across all agent threads (used by thread-list SSE).
    /// The callback receives (agentId, message). Returns an unsubscribe action.
    /// </summary>
    public Action SubscribeAllDm(Action<string, DmMessage> callback)
    {
        lock (_lock)
        {
            _globalDmSubscribers.Add(callback);
        }

        return () =>
        {
            lock (_lock)
            {
                _globalDmSubscribers.Remove(callback);
            }
        };
    }

    /// <summary>
    /// Returns the number of active global DM subscribers (for diagnostics).
    /// </summary>
    public int GetGlobalDmSubscriberCount()
    {
        lock (_lock)
        {
            return _globalDmSubscribers.Count;
        }
    }
}
