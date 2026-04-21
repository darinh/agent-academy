using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="TaskItemService"/> — task item CRUD and queries.
/// </summary>
public sealed class TaskItemServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly TaskItemService _sut;

    public TaskItemServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new TaskItemService(_db, NullLogger<TaskItemService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private RoomEntity AddRoom(string? id = null, string? workspacePath = null)
    {
        var room = new RoomEntity
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Name = "Test Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            WorkspacePath = workspacePath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Rooms.Add(room);
        _db.SaveChanges();
        return room;
    }

    private TaskItemEntity AddTaskItem(
        string roomId,
        string? breakoutRoomId = null,
        string status = "Pending",
        string? evidence = null,
        string? feedback = null,
        DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        var entity = new TaskItemEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "Test Task",
            Description = "Test Description",
            Status = status,
            AssignedTo = "agent-1",
            RoomId = roomId,
            BreakoutRoomId = breakoutRoomId,
            Evidence = evidence,
            Feedback = feedback,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.TaskItems.Add(entity);
        _db.SaveChanges();
        return entity;
    }

    private WorkspaceEntity AddWorkspace(string path, bool isActive = false)
    {
        var workspace = new WorkspaceEntity
        {
            Path = path,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        };
        _db.Workspaces.Add(workspace);
        _db.SaveChanges();
        return workspace;
    }

    // ── CreateTaskItemAsync ──────────────────────────────────────

    [Fact]
    public async Task Create_ReturnsTaskItemWithGeneratedId()
    {
        var result = await _sut.CreateTaskItemAsync(
            "Task 1", "Desc", "agent-1", "room-1", null);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Id));
    }

    [Fact]
    public async Task Create_StatusIsPending()
    {
        var result = await _sut.CreateTaskItemAsync(
            "Task 1", "Desc", "agent-1", "room-1", null);

        Assert.Equal(TaskItemStatus.Pending, result.Status);
    }

    [Fact]
    public async Task Create_SetsCreatedAtAndUpdatedAt()
    {
        var before = DateTime.UtcNow;

        var result = await _sut.CreateTaskItemAsync(
            "Task 1", "Desc", "agent-1", "room-1", null);

        Assert.InRange(result.CreatedAt, before, DateTime.UtcNow.AddSeconds(1));
        Assert.InRange(result.UpdatedAt, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Create_PersistsToDatabase()
    {
        var result = await _sut.CreateTaskItemAsync(
            "Task 1", "Desc", "agent-1", "room-1", null);

        _db.ChangeTracker.Clear();
        var entity = await _db.TaskItems.FindAsync(result.Id);
        Assert.NotNull(entity);
        Assert.Equal("Task 1", entity.Title);
        Assert.Equal("Desc", entity.Description);
        Assert.Equal("agent-1", entity.AssignedTo);
        Assert.Equal("room-1", entity.RoomId);
    }

    [Fact]
    public async Task Create_WithBreakoutRoomId_SetsCorrectly()
    {
        var result = await _sut.CreateTaskItemAsync(
            "Task 1", "Desc", "agent-1", "room-1", "breakout-1");

        Assert.Equal("breakout-1", result.BreakoutRoomId);

        _db.ChangeTracker.Clear();
        var entity = await _db.TaskItems.FindAsync(result.Id);
        Assert.Equal("breakout-1", entity!.BreakoutRoomId);
    }

    [Fact]
    public async Task Create_WithNullBreakoutRoomId_SetsNull()
    {
        var result = await _sut.CreateTaskItemAsync(
            "Task 1", "Desc", "agent-1", "room-1", null);

        Assert.Null(result.BreakoutRoomId);
    }

    // ── UpdateTaskItemStatusAsync ────────────────────────────────

    [Fact]
    public async Task UpdateStatus_ItemNotFound_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateTaskItemStatusAsync("nonexistent", TaskItemStatus.Active));
    }

    [Fact]
    public async Task UpdateStatus_UpdatesStatusField()
    {
        var room = AddRoom();
        var item = AddTaskItem(room.Id);

        await _sut.UpdateTaskItemStatusAsync(item.Id, TaskItemStatus.Active);

        _db.ChangeTracker.Clear();
        var entity = await _db.TaskItems.FindAsync(item.Id);
        Assert.Equal("Active", entity!.Status);
    }

    [Fact]
    public async Task UpdateStatus_UpdatesUpdatedAt()
    {
        var room = AddRoom();
        var item = AddTaskItem(room.Id);
        var originalTime = item.UpdatedAt;

        await Task.Delay(10);
        await _sut.UpdateTaskItemStatusAsync(item.Id, TaskItemStatus.Active);

        _db.ChangeTracker.Clear();
        var entity = await _db.TaskItems.FindAsync(item.Id);
        Assert.True(entity!.UpdatedAt > originalTime);
    }

    [Fact]
    public async Task UpdateStatus_WithEvidence_SetsEvidence()
    {
        var room = AddRoom();
        var item = AddTaskItem(room.Id);

        await _sut.UpdateTaskItemStatusAsync(item.Id, TaskItemStatus.Done, "Build passes");

        _db.ChangeTracker.Clear();
        var entity = await _db.TaskItems.FindAsync(item.Id);
        Assert.Equal("Build passes", entity!.Evidence);
    }

    [Fact]
    public async Task UpdateStatus_WithNullEvidence_DoesNotClearExistingEvidence()
    {
        var room = AddRoom();
        var item = AddTaskItem(room.Id, evidence: "Original evidence");

        await _sut.UpdateTaskItemStatusAsync(item.Id, TaskItemStatus.Active);

        _db.ChangeTracker.Clear();
        var entity = await _db.TaskItems.FindAsync(item.Id);
        Assert.Equal("Original evidence", entity!.Evidence);
    }

    [Fact]
    public async Task UpdateStatus_ToDone_Works()
    {
        var room = AddRoom();
        var item = AddTaskItem(room.Id);

        await _sut.UpdateTaskItemStatusAsync(item.Id, TaskItemStatus.Done);

        _db.ChangeTracker.Clear();
        var entity = await _db.TaskItems.FindAsync(item.Id);
        Assert.Equal("Done", entity!.Status);
    }

    [Fact]
    public async Task UpdateStatus_ToRejected_Works()
    {
        var room = AddRoom();
        var item = AddTaskItem(room.Id);

        await _sut.UpdateTaskItemStatusAsync(item.Id, TaskItemStatus.Rejected);

        _db.ChangeTracker.Clear();
        var entity = await _db.TaskItems.FindAsync(item.Id);
        Assert.Equal("Rejected", entity!.Status);
    }

    [Fact]
    public async Task UpdateStatus_ToActive_Works()
    {
        var room = AddRoom();
        var item = AddTaskItem(room.Id);

        await _sut.UpdateTaskItemStatusAsync(item.Id, TaskItemStatus.Active);

        _db.ChangeTracker.Clear();
        var entity = await _db.TaskItems.FindAsync(item.Id);
        Assert.Equal("Active", entity!.Status);
    }

    // ── GetBreakoutTaskItemsAsync ────────────────────────────────

    [Fact]
    public async Task GetBreakoutItems_NoItems_ReturnsEmpty()
    {
        var result = await _sut.GetBreakoutTaskItemsAsync("breakout-nonexistent");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBreakoutItems_ReturnsOnlyMatchingBreakout()
    {
        var room = AddRoom();
        AddTaskItem(room.Id, breakoutRoomId: "breakout-1");
        AddTaskItem(room.Id, breakoutRoomId: "breakout-1");
        AddTaskItem(room.Id, breakoutRoomId: "breakout-2");

        var result = await _sut.GetBreakoutTaskItemsAsync("breakout-1");

        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Equal("breakout-1", item.BreakoutRoomId));
    }

    [Fact]
    public async Task GetBreakoutItems_DoesNotReturnItemsFromOtherBreakouts()
    {
        var room = AddRoom();
        AddTaskItem(room.Id, breakoutRoomId: "breakout-A");
        AddTaskItem(room.Id, breakoutRoomId: "breakout-B");
        AddTaskItem(room.Id); // no breakout

        var result = await _sut.GetBreakoutTaskItemsAsync("breakout-A");

        Assert.Single(result);
        Assert.Equal("breakout-A", result[0].BreakoutRoomId);
    }

    // ── GetActiveTaskItemsAsync ──────────────────────────────────

    [Fact]
    public async Task GetActiveItems_NoItems_ReturnsEmpty()
    {
        var result = await _sut.GetActiveTaskItemsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActiveItems_ReturnsPendingAndActive()
    {
        var room = AddRoom();
        AddTaskItem(room.Id, status: "Pending");
        AddTaskItem(room.Id, status: "Active");

        var result = await _sut.GetActiveTaskItemsAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetActiveItems_ExcludesDoneAndRejected()
    {
        var room = AddRoom();
        AddTaskItem(room.Id, status: "Pending");
        AddTaskItem(room.Id, status: "Done");
        AddTaskItem(room.Id, status: "Rejected");

        var result = await _sut.GetActiveTaskItemsAsync();

        Assert.Single(result);
        Assert.Equal(TaskItemStatus.Pending, result[0].Status);
    }

    [Fact]
    public async Task GetActiveItems_OrderedByCreatedAt()
    {
        var room = AddRoom();
        var older = DateTime.UtcNow.AddMinutes(-10);
        var newer = DateTime.UtcNow;
        AddTaskItem(room.Id, status: "Pending", createdAt: newer);
        AddTaskItem(room.Id, status: "Active", createdAt: older);

        var result = await _sut.GetActiveTaskItemsAsync();

        Assert.Equal(2, result.Count);
        Assert.True(result[0].CreatedAt <= result[1].CreatedAt);
    }

    [Fact]
    public async Task GetActiveItems_WithActiveWorkspace_ScopesToWorkspace()
    {
        var ws = AddWorkspace("/test/workspace", isActive: true);
        var roomInWs = AddRoom(workspacePath: ws.Path);
        var roomOutside = AddRoom();
        AddTaskItem(roomInWs.Id, status: "Pending");
        AddTaskItem(roomOutside.Id, status: "Pending");

        var result = await _sut.GetActiveTaskItemsAsync();

        Assert.Single(result);
        Assert.Equal(roomInWs.Id, result[0].RoomId);
    }

    [Fact]
    public async Task GetActiveItems_NoActiveWorkspace_ReturnsAll()
    {
        AddWorkspace("/test/workspace", isActive: false);
        var room1 = AddRoom();
        var room2 = AddRoom();
        AddTaskItem(room1.Id, status: "Pending");
        AddTaskItem(room2.Id, status: "Active");

        var result = await _sut.GetActiveTaskItemsAsync();

        Assert.Equal(2, result.Count);
    }

    // ── GetTaskItemAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetItem_NotFound_ReturnsNull()
    {
        var result = await _sut.GetTaskItemAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetItem_Found_ReturnsTaskItem()
    {
        var room = AddRoom();
        var item = AddTaskItem(room.Id);

        var result = await _sut.GetTaskItemAsync(item.Id);

        Assert.NotNull(result);
        Assert.Equal(item.Id, result.Id);
    }

    [Fact]
    public async Task GetItem_MapsAllFieldsCorrectly()
    {
        var room = AddRoom();
        var item = AddTaskItem(room.Id, breakoutRoomId: "br-1",
            evidence: "Evidence text", feedback: "Feedback text");

        var result = await _sut.GetTaskItemAsync(item.Id);

        Assert.NotNull(result);
        Assert.Equal(item.Id, result.Id);
        Assert.Equal("Test Task", result.Title);
        Assert.Equal("Test Description", result.Description);
        Assert.Equal(TaskItemStatus.Pending, result.Status);
        Assert.Equal("agent-1", result.AssignedTo);
        Assert.Equal(room.Id, result.RoomId);
        Assert.Equal("br-1", result.BreakoutRoomId);
        Assert.Equal("Evidence text", result.Evidence);
        Assert.Equal("Feedback text", result.Feedback);
        Assert.Equal(item.CreatedAt, result.CreatedAt);
        Assert.Equal(item.UpdatedAt, result.UpdatedAt);
    }

    // ── GetTaskItemsAsync ────────────────────────────────────────

    [Fact]
    public async Task GetItems_NoFilter_ReturnsAll()
    {
        var room = AddRoom();
        AddTaskItem(room.Id, status: "Pending");
        AddTaskItem(room.Id, status: "Done");
        AddTaskItem(room.Id, status: "Active");

        var result = await _sut.GetTaskItemsAsync();

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetItems_ByRoomId_FiltersCorrectly()
    {
        var room1 = AddRoom();
        var room2 = AddRoom();
        AddTaskItem(room1.Id);
        AddTaskItem(room1.Id);
        AddTaskItem(room2.Id);

        var result = await _sut.GetTaskItemsAsync(roomId: room1.Id);

        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Equal(room1.Id, item.RoomId));
    }

    [Fact]
    public async Task GetItems_ByRoomId_MatchesBreakoutRoomId()
    {
        var room = AddRoom();
        AddTaskItem(room.Id, breakoutRoomId: "br-target");
        AddTaskItem(room.Id, breakoutRoomId: "br-other");

        var result = await _sut.GetTaskItemsAsync(roomId: "br-target");

        Assert.Single(result);
        Assert.Equal("br-target", result[0].BreakoutRoomId);
    }

    [Fact]
    public async Task GetItems_ByStatus_FiltersCorrectly()
    {
        var room = AddRoom();
        AddTaskItem(room.Id, status: "Pending");
        AddTaskItem(room.Id, status: "Active");
        AddTaskItem(room.Id, status: "Done");

        var result = await _sut.GetTaskItemsAsync(status: TaskItemStatus.Pending);

        Assert.Single(result);
        Assert.Equal(TaskItemStatus.Pending, result[0].Status);
    }

    [Fact]
    public async Task GetItems_ByRoomAndStatus_CombinesFilters()
    {
        var room1 = AddRoom();
        var room2 = AddRoom();
        AddTaskItem(room1.Id, status: "Pending");
        AddTaskItem(room1.Id, status: "Done");
        AddTaskItem(room2.Id, status: "Pending");

        var result = await _sut.GetTaskItemsAsync(
            roomId: room1.Id, status: TaskItemStatus.Pending);

        Assert.Single(result);
        Assert.Equal(room1.Id, result[0].RoomId);
        Assert.Equal(TaskItemStatus.Pending, result[0].Status);
    }

    [Fact]
    public async Task GetItems_OrderedByCreatedAt()
    {
        var room = AddRoom();
        AddTaskItem(room.Id, createdAt: DateTime.UtcNow.AddMinutes(5));
        AddTaskItem(room.Id, createdAt: DateTime.UtcNow.AddMinutes(-5));

        var result = await _sut.GetTaskItemsAsync(roomId: room.Id);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].CreatedAt <= result[1].CreatedAt);
    }

    [Fact]
    public async Task GetItems_NoRoomFilter_ScopesToActiveWorkspace()
    {
        var ws = AddWorkspace("/scoped/workspace", isActive: true);
        var roomInWs = AddRoom(workspacePath: ws.Path);
        var roomOutside = AddRoom();
        AddTaskItem(roomInWs.Id);
        AddTaskItem(roomOutside.Id);

        var result = await _sut.GetTaskItemsAsync();

        Assert.Single(result);
        Assert.Equal(roomInWs.Id, result[0].RoomId);
    }

    [Fact]
    public async Task GetItems_NoRoomFilter_NoWorkspace_ReturnsAll()
    {
        var room1 = AddRoom();
        var room2 = AddRoom();
        AddTaskItem(room1.Id);
        AddTaskItem(room2.Id);

        var result = await _sut.GetTaskItemsAsync();

        Assert.Equal(2, result.Count);
    }
}
