using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

public sealed class SignalRConnectionTrackerTests
{
    private readonly SignalRConnectionTracker _sut = new();

    // ── Connect / Disconnect ─────────────────────────────────────────

    [Fact]
    public void OnConnected_AddsConnection()
    {
        _sut.OnConnected("conn-1", "user-A");

        Assert.Equal(1, _sut.Count);
        var conn = Assert.Single(_sut.GetConnections());
        Assert.Equal("conn-1", conn.ConnectionId);
        Assert.Equal("user-A", conn.UserId);
    }

    [Fact]
    public void OnConnected_NullUserId_Accepted()
    {
        _sut.OnConnected("conn-2", null);

        var conn = Assert.Single(_sut.GetConnections());
        Assert.Null(conn.UserId);
    }

    [Fact]
    public void OnDisconnected_RemovesConnection()
    {
        _sut.OnConnected("conn-3", "user-B");
        _sut.OnDisconnected("conn-3");

        Assert.Equal(0, _sut.Count);
        Assert.Empty(_sut.GetConnections());
    }

    [Fact]
    public void OnDisconnected_UnknownId_DoesNotThrow()
    {
        _sut.OnDisconnected("nonexistent");
        Assert.Equal(0, _sut.Count);
    }

    [Fact]
    public void OnConnected_SameIdTwice_OverwritesPrevious()
    {
        _sut.OnConnected("conn-4", "user-C");
        _sut.OnConnected("conn-4", "user-D");

        Assert.Equal(1, _sut.Count);
        Assert.Equal("user-D", _sut.GetConnections()[0].UserId);
    }

    // ── Multiple connections ─────────────────────────────────────────

    [Fact]
    public void MultipleConnections_TrackedIndependently()
    {
        _sut.OnConnected("conn-A", "user-1");
        _sut.OnConnected("conn-B", "user-2");
        _sut.OnConnected("conn-C", "user-1");

        Assert.Equal(3, _sut.Count);
    }

    [Fact]
    public void MultipleConnections_DisconnectOne_OnlyRemovesThat()
    {
        _sut.OnConnected("conn-A", "user-1");
        _sut.OnConnected("conn-B", "user-2");
        _sut.OnDisconnected("conn-A");

        Assert.Equal(1, _sut.Count);
        Assert.Equal("conn-B", _sut.GetConnections()[0].ConnectionId);
    }

    // ── GetConnections snapshot ──────────────────────────────────────

    [Fact]
    public void GetConnections_ReturnsReadOnlySnapshot()
    {
        _sut.OnConnected("conn-1", "user-1");
        var snapshot = _sut.GetConnections();

        _sut.OnConnected("conn-2", "user-2");

        // Snapshot should not reflect new connection
        Assert.Single(snapshot);
    }

    [Fact]
    public void GetConnections_Empty_ReturnsEmptyList()
    {
        Assert.Empty(_sut.GetConnections());
    }

    // ── ConnectedAt timestamp ────────────────────────────────────────

    [Fact]
    public void OnConnected_SetsConnectedAtTimestamp()
    {
        var before = DateTime.UtcNow;
        _sut.OnConnected("conn-1", "user-1");
        var after = DateTime.UtcNow;

        var conn = _sut.GetConnections()[0];
        Assert.InRange(conn.ConnectedAt, before, after);
    }
}
