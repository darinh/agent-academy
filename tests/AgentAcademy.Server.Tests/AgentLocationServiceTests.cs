using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

public class AgentLocationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ActivityBroadcaster _bus;
    private readonly ActivityPublisher _activity;
    private readonly AgentCatalogOptions _catalog;
    private readonly AgentLocationService _sut;

    private const string CatalogAgentId = "planner-1";
    private const string RoomId = "room-main";

    public AgentLocationServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _bus = new ActivityBroadcaster();
        _activity = new ActivityPublisher(_db, _bus);
        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
                new AgentDefinition(
                    Id: CatalogAgentId, Name: "Aristotle", Role: "Planner",
                    Summary: "Planner agent", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true)
            ]
        );

        _sut = new AgentLocationService(_db, _catalog, _activity);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void EnsureRoom(string roomId = RoomId)
    {
        if (_db.Rooms.Find(roomId) is not null) return;
        _db.Rooms.Add(new RoomEntity
        {
            Id = roomId,
            Name = "Test Room",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    private AgentLocationEntity SeedLocation(
        string agentId = CatalogAgentId,
        string roomId = RoomId,
        string state = "Idle",
        string? breakoutRoomId = null)
    {
        var entity = new AgentLocationEntity
        {
            AgentId = agentId,
            RoomId = roomId,
            State = state,
            BreakoutRoomId = breakoutRoomId,
            UpdatedAt = DateTime.UtcNow
        };
        _db.AgentLocations.Add(entity);
        _db.SaveChanges();
        return entity;
    }

    private AgentConfigEntity SeedCustomAgent(string agentId)
    {
        var config = new AgentConfigEntity
        {
            AgentId = agentId,
            UpdatedAt = DateTime.UtcNow
        };
        _db.AgentConfigs.Add(config);
        _db.SaveChanges();
        return config;
    }

    // ── GetAgentLocationsAsync ──────────────────────────────

    [Fact]
    public async Task GetAgentLocations_EmptyDb_ReturnsEmptyList()
    {
        var result = await _sut.GetAgentLocationsAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentLocations_ReturnsAllLocations()
    {
        SeedLocation(agentId: CatalogAgentId, state: "Working");
        SeedLocation(agentId: "other-agent", state: "Idle");

        var result = await _sut.GetAgentLocationsAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetAgentLocations_MapsStateStringToEnum()
    {
        SeedLocation(state: "Working");

        var result = await _sut.GetAgentLocationsAsync();

        Assert.Single(result);
        Assert.Equal(AgentState.Working, result[0].State);
    }

    // ── GetAgentLocationAsync ───────────────────────────────

    [Fact]
    public async Task GetAgentLocation_NotFound_ReturnsNull()
    {
        var result = await _sut.GetAgentLocationAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAgentLocation_Found_ReturnsModel()
    {
        SeedLocation(agentId: CatalogAgentId, roomId: RoomId, state: "InRoom");

        var result = await _sut.GetAgentLocationAsync(CatalogAgentId);

        Assert.NotNull(result);
        Assert.Equal(CatalogAgentId, result.AgentId);
        Assert.Equal(RoomId, result.RoomId);
        Assert.Equal(AgentState.InRoom, result.State);
    }

    [Fact]
    public async Task GetAgentLocation_MapsBreakoutRoomId()
    {
        SeedLocation(breakoutRoomId: "breakout-42");

        var result = await _sut.GetAgentLocationAsync(CatalogAgentId);

        Assert.NotNull(result);
        Assert.Equal("breakout-42", result.BreakoutRoomId);
    }

    // ── MoveAgentAsync ──────────────────────────────────────

    [Fact]
    public async Task MoveAgent_CatalogAgent_NoExistingLocation_CreatesNew()
    {
        EnsureRoom();

        var result = await _sut.MoveAgentAsync(CatalogAgentId, RoomId, AgentState.Working);

        Assert.Equal(CatalogAgentId, result.AgentId);
        Assert.Equal(RoomId, result.RoomId);
        Assert.Equal(AgentState.Working, result.State);

        var entity = await _db.AgentLocations.FindAsync(CatalogAgentId);
        Assert.NotNull(entity);
    }

    [Fact]
    public async Task MoveAgent_CatalogAgent_ExistingLocation_Updates()
    {
        EnsureRoom();
        EnsureRoom("room-2");
        SeedLocation(state: "Idle");

        var result = await _sut.MoveAgentAsync(CatalogAgentId, "room-2", AgentState.Presenting);

        Assert.Equal("room-2", result.RoomId);
        Assert.Equal(AgentState.Presenting, result.State);

        Assert.Equal(1, await _db.AgentLocations.CountAsync());
    }

    [Fact]
    public async Task MoveAgent_SetsFieldsOnCreate()
    {
        EnsureRoom();

        var result = await _sut.MoveAgentAsync(
            CatalogAgentId, RoomId, AgentState.Working, breakoutRoomId: "br-1");

        Assert.Equal(RoomId, result.RoomId);
        Assert.Equal(AgentState.Working, result.State);
        Assert.Equal("br-1", result.BreakoutRoomId);
    }

    [Fact]
    public async Task MoveAgent_SetsFieldsOnUpdate()
    {
        EnsureRoom();
        EnsureRoom("room-new");
        SeedLocation(state: "Idle", breakoutRoomId: "old-br");

        var result = await _sut.MoveAgentAsync(
            CatalogAgentId, "room-new", AgentState.Presenting, breakoutRoomId: "new-br");

        Assert.Equal("room-new", result.RoomId);
        Assert.Equal(AgentState.Presenting, result.State);
        Assert.Equal("new-br", result.BreakoutRoomId);
    }

    [Fact]
    public async Task MoveAgent_ClearsBreakoutRoomIdWhenNull()
    {
        EnsureRoom();
        SeedLocation(breakoutRoomId: "old-br");

        var result = await _sut.MoveAgentAsync(
            CatalogAgentId, RoomId, AgentState.InRoom, breakoutRoomId: null);

        Assert.Null(result.BreakoutRoomId);

        var entity = await _db.AgentLocations.FindAsync(CatalogAgentId);
        Assert.Null(entity!.BreakoutRoomId);
    }

    [Fact]
    public async Task MoveAgent_UnknownAgent_ThrowsInvalidOperation()
    {
        EnsureRoom();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.MoveAgentAsync("unknown-agent", RoomId, AgentState.Working));

        Assert.Contains("unknown-agent", ex.Message);
    }

    [Fact]
    public async Task MoveAgent_CustomAgentInConfigs_Succeeds()
    {
        EnsureRoom();
        SeedCustomAgent("custom-agent-1");

        var result = await _sut.MoveAgentAsync("custom-agent-1", RoomId, AgentState.Idle);

        Assert.Equal("custom-agent-1", result.AgentId);
        Assert.Equal(RoomId, result.RoomId);
    }

    [Fact]
    public async Task MoveAgent_PublishesPresenceUpdatedEvent()
    {
        EnsureRoom();
        ActivityEvent? captured = null;
        _bus.Subscribe(e => captured = e);

        await _sut.MoveAgentAsync(CatalogAgentId, RoomId, AgentState.Working);

        Assert.NotNull(captured);
        Assert.Equal(ActivityEventType.PresenceUpdated, captured.Type);
        Assert.Contains(CatalogAgentId, captured.Message);
    }

    [Fact]
    public async Task MoveAgent_ReturnsAgentLocationModel()
    {
        EnsureRoom();

        var result = await _sut.MoveAgentAsync(CatalogAgentId, RoomId, AgentState.Idle);

        Assert.IsType<AgentLocation>(result);
        Assert.Equal(CatalogAgentId, result.AgentId);
    }

    // ── BuildAgentLocation (internal static) ────────────────

    [Fact]
    public void BuildAgentLocation_MapsAllFields()
    {
        var now = DateTime.UtcNow;
        var entity = new AgentLocationEntity
        {
            AgentId = "agent-x",
            RoomId = "room-y",
            State = "Presenting",
            BreakoutRoomId = "br-z",
            UpdatedAt = now
        };

        var result = AgentLocationService.BuildAgentLocation(entity);

        Assert.Equal("agent-x", result.AgentId);
        Assert.Equal("room-y", result.RoomId);
        Assert.Equal(AgentState.Presenting, result.State);
        Assert.Equal("br-z", result.BreakoutRoomId);
        Assert.Equal(now, result.UpdatedAt);
    }

    [Theory]
    [InlineData("InRoom", AgentState.InRoom)]
    [InlineData("Working", AgentState.Working)]
    [InlineData("Presenting", AgentState.Presenting)]
    [InlineData("Idle", AgentState.Idle)]
    public void BuildAgentLocation_ParsesStateEnum(string stateStr, AgentState expected)
    {
        var entity = new AgentLocationEntity
        {
            AgentId = "a",
            RoomId = "r",
            State = stateStr,
            UpdatedAt = DateTime.UtcNow
        };

        var result = AgentLocationService.BuildAgentLocation(entity);

        Assert.Equal(expected, result.State);
    }
}
