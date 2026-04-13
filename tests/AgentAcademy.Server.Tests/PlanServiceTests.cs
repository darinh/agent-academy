using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="PlanService"/> — plan CRUD for rooms and breakout rooms.
/// </summary>
public sealed class PlanServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly PlanService _sut;

    public PlanServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new PlanService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private RoomEntity AddRoom(string? id = null)
    {
        var room = new RoomEntity
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Name = "Test Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Rooms.Add(room);
        _db.SaveChanges();
        return room;
    }

    private BreakoutRoomEntity AddBreakoutRoom(string parentRoomId, string? id = null)
    {
        var breakout = new BreakoutRoomEntity
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Name = "Breakout Room",
            ParentRoomId = parentRoomId,
            AssignedAgentId = "agent-1",
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.BreakoutRooms.Add(breakout);
        _db.SaveChanges();
        return breakout;
    }

    private PlanEntity AddPlan(string roomId, string content = "Plan content")
    {
        var plan = new PlanEntity
        {
            RoomId = roomId,
            Content = content,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Plans.Add(plan);
        _db.SaveChanges();
        return plan;
    }

    // ── GetPlanAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetPlan_NoPlan_ReturnsNull()
    {
        var result = await _sut.GetPlanAsync("nonexistent-room");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPlan_PlanExists_ReturnsPlanContent()
    {
        var room = AddRoom();
        AddPlan(room.Id, "My plan");

        var result = await _sut.GetPlanAsync(room.Id);

        Assert.NotNull(result);
        Assert.IsType<PlanContent>(result);
    }

    [Fact]
    public async Task GetPlan_ReturnsCorrectContent()
    {
        var room = AddRoom();
        AddPlan(room.Id, "Detailed plan for sprint 1");

        var result = await _sut.GetPlanAsync(room.Id);

        Assert.Equal("Detailed plan for sprint 1", result!.Content);
    }

    // ── SetPlanAsync — validation ────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetPlan_InvalidRoomId_ThrowsArgumentException(string? roomId)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.SetPlanAsync(roomId!, "content"));

        Assert.Equal("roomId", ex.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetPlan_InvalidContent_ThrowsArgumentException(string? content)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.SetPlanAsync("room-1", content!));

        Assert.Equal("content", ex.ParamName);
    }

    [Fact]
    public async Task SetPlan_RoomDoesNotExist_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SetPlanAsync("nonexistent", "content"));
    }

    // ── SetPlanAsync — create ────────────────────────────────────

    [Fact]
    public async Task SetPlan_NewPlan_CreatesPlanEntity()
    {
        var room = AddRoom();

        await _sut.SetPlanAsync(room.Id, "New plan");

        _db.ChangeTracker.Clear();
        var entity = await _db.Plans.FindAsync(room.Id);
        Assert.NotNull(entity);
        Assert.Equal("New plan", entity.Content);
    }

    [Fact]
    public async Task SetPlan_NewPlan_SetsUpdatedAt()
    {
        var room = AddRoom();
        var before = DateTime.UtcNow;

        await _sut.SetPlanAsync(room.Id, "New plan");

        _db.ChangeTracker.Clear();
        var entity = await _db.Plans.FindAsync(room.Id);
        Assert.NotNull(entity);
        Assert.InRange(entity.UpdatedAt, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task SetPlan_ForBreakoutRoom_Works()
    {
        var room = AddRoom();
        var breakout = AddBreakoutRoom(room.Id);

        await _sut.SetPlanAsync(breakout.Id, "Breakout plan");

        _db.ChangeTracker.Clear();
        var entity = await _db.Plans.FindAsync(breakout.Id);
        Assert.NotNull(entity);
        Assert.Equal("Breakout plan", entity.Content);
    }

    // ── SetPlanAsync — update ────────────────────────────────────

    [Fact]
    public async Task SetPlan_ExistingPlan_UpdatesContent()
    {
        var room = AddRoom();
        AddPlan(room.Id, "Old content");

        await _sut.SetPlanAsync(room.Id, "Updated content");

        _db.ChangeTracker.Clear();
        var entity = await _db.Plans.FindAsync(room.Id);
        Assert.Equal("Updated content", entity!.Content);
    }

    [Fact]
    public async Task SetPlan_ExistingPlan_UpdatesTimestamp()
    {
        var room = AddRoom();
        var plan = AddPlan(room.Id);
        var originalTime = plan.UpdatedAt;

        await Task.Delay(10); // ensure time difference
        await _sut.SetPlanAsync(room.Id, "Updated content");

        _db.ChangeTracker.Clear();
        var entity = await _db.Plans.FindAsync(room.Id);
        Assert.True(entity!.UpdatedAt > originalTime);
    }

    [Fact]
    public async Task SetPlan_DoesNotAffectOtherPlans()
    {
        var room1 = AddRoom();
        var room2 = AddRoom();
        AddPlan(room1.Id, "Room 1 plan");
        AddPlan(room2.Id, "Room 2 plan");

        await _sut.SetPlanAsync(room1.Id, "Updated room 1 plan");

        _db.ChangeTracker.Clear();
        var otherPlan = await _db.Plans.FindAsync(room2.Id);
        Assert.Equal("Room 2 plan", otherPlan!.Content);
    }

    // ── DeletePlanAsync ──────────────────────────────────────────

    [Fact]
    public async Task DeletePlan_NoPlan_ReturnsFalse()
    {
        var result = await _sut.DeletePlanAsync("nonexistent-room");

        Assert.False(result);
    }

    [Fact]
    public async Task DeletePlan_PlanExists_ReturnsTrue()
    {
        var room = AddRoom();
        AddPlan(room.Id);

        var result = await _sut.DeletePlanAsync(room.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task DeletePlan_PlanExists_RemovesFromDb()
    {
        var room = AddRoom();
        AddPlan(room.Id);

        await _sut.DeletePlanAsync(room.Id);

        _db.ChangeTracker.Clear();
        var entity = await _db.Plans.FindAsync(room.Id);
        Assert.Null(entity);
    }

    [Fact]
    public async Task DeletePlan_DoesNotAffectOtherPlans()
    {
        var room1 = AddRoom();
        var room2 = AddRoom();
        AddPlan(room1.Id, "Room 1 plan");
        AddPlan(room2.Id, "Room 2 plan");

        await _sut.DeletePlanAsync(room1.Id);

        _db.ChangeTracker.Clear();
        var otherPlan = await _db.Plans.FindAsync(room2.Id);
        Assert.NotNull(otherPlan);
        Assert.Equal("Room 2 plan", otherPlan.Content);
    }
}
