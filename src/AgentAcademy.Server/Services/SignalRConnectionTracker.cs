using System.Collections.Concurrent;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Tracks active SignalR connections for the current server instance.
/// Injected into <see cref="Hubs.ActivityHub"/> and read by the
/// SHOW_ACTIVE_CONNECTIONS command handler.
/// </summary>
public sealed class SignalRConnectionTracker
{
    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();

    public void OnConnected(string connectionId, string? userId)
    {
        _connections[connectionId] = new ConnectionInfo(connectionId, userId, DateTime.UtcNow);
    }

    public void OnDisconnected(string connectionId)
    {
        _connections.TryRemove(connectionId, out _);
    }

    public IReadOnlyList<ConnectionInfo> GetConnections() =>
        _connections.Values.ToList().AsReadOnly();

    public int Count => _connections.Count;
}

public sealed record ConnectionInfo(
    string ConnectionId,
    string? UserId,
    DateTime ConnectedAt);
