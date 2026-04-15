using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for broadcasting room and DM messages to SSE subscribers.
/// </summary>
public interface IMessageBroadcaster
{
    /// <summary>
    /// Broadcasts a message to all subscribers of the given room.
    /// </summary>
    void Broadcast(string roomId, ChatEnvelope message);

    /// <summary>
    /// Subscribes to messages in a specific room. Returns an unsubscribe action.
    /// </summary>
    Action Subscribe(string roomId, Action<ChatEnvelope> callback);

    /// <summary>
    /// Returns the number of active subscribers for a room (for diagnostics).
    /// </summary>
    int GetSubscriberCount(string roomId);

    /// <summary>
    /// Broadcasts a DM to all subscribers of the given agent's DM thread.
    /// </summary>
    void BroadcastDm(string agentId, DmMessage message);

    /// <summary>
    /// Subscribes to DM messages in a specific agent's thread. Returns an unsubscribe action.
    /// </summary>
    Action SubscribeDm(string agentId, Action<DmMessage> callback);

    /// <summary>
    /// Returns the number of active DM subscribers for an agent thread (for diagnostics).
    /// </summary>
    int GetDmSubscriberCount(string agentId);

    /// <summary>
    /// Subscribes to ALL DM messages across all agent threads.
    /// The callback receives (agentId, message). Returns an unsubscribe action.
    /// </summary>
    Action SubscribeAllDm(Action<string, DmMessage> callback);

    /// <summary>
    /// Returns the number of active global DM subscribers (for diagnostics).
    /// </summary>
    int GetGlobalDmSubscriberCount();
}
