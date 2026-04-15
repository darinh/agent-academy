using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

public sealed class BreakoutRoomServiceTests : IDisposable
{
    private static readonly List<AgentDefinition> TestAgents =
    [
        new("agent-1", "Agent One", "Engineer", "Test agent 1", "", null, [], [], true),
        new("agent-2", "Agent Two", "Reviewer", "Test agent 2", "", null, [], [], true),
    ];

    private readonly TestServiceGraph _graph;
    private AgentAcademyDbContext Db => _graph.Db;
    private IBreakoutRoomService Sut => _graph.BreakoutRoomService;

    public BreakoutRoomServiceTests()
    {
        _graph = new TestServiceGraph(TestAgents);
    }

    public void Dispose() => _graph.Dispose();

    // ── Helpers ──────────────────────────────────────────────────

    private RoomEntity AddRoom(string? id = null, string? workspacePath = null)
    {
        var room = new RoomEntity
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Name = "Test Room",
            Status = "Active",
            CurrentPhase = "Execution",
            WorkspacePath = workspacePath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        Db.Rooms.Add(room);
        Db.SaveChanges();
        return room;
    }

    private BreakoutRoomEntity AddBreakout(
        string parentRoomId,
        string agentId,
        string status = "Active",
        string? taskId = null,
        string? closeReason = null,
        DateTime? updatedAt = null)
    {
        var entity = new BreakoutRoomEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"Breakout-{Guid.NewGuid().ToString("N")[..6]}",
            ParentRoomId = parentRoomId,
            AssignedAgentId = agentId,
            Status = status,
            TaskId = taskId,
            CloseReason = closeReason,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = updatedAt ?? DateTime.UtcNow
        };
        Db.BreakoutRooms.Add(entity);
        Db.SaveChanges();
        return entity;
    }

    private TaskEntity AddTask(string? roomId = null, string? assignedAgentId = null, string status = "Active")
    {
        var entity = new TaskEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "Test Task",
            Description = "A test task",
            SuccessCriteria = "",
            Status = status,
            Type = "Feature",
            CurrentPhase = "Implementation",
            CurrentPlan = "",
            ValidationStatus = "NotStarted",
            ValidationSummary = "",
            ImplementationStatus = "InProgress",
            ImplementationSummary = "",
            PreferredRoles = "[]",
            RoomId = roomId,
            AssignedAgentId = assignedAgentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        Db.Tasks.Add(entity);
        Db.SaveChanges();
        return entity;
    }

    private BreakoutMessageEntity AddMessage(string breakoutId, string content = "Hello")
    {
        var msg = new BreakoutMessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            BreakoutRoomId = breakoutId,
            SenderId = "agent-1",
            SenderName = "Agent One",
            SenderRole = "Engineer",
            SenderKind = "Agent",
            Kind = nameof(MessageKind.Coordination),
            Content = content,
            SentAt = DateTime.UtcNow
        };
        Db.BreakoutMessages.Add(msg);
        Db.SaveChanges();
        return msg;
    }

    private WorkspaceEntity AddWorkspace(string path, bool isActive = true)
    {
        var ws = new WorkspaceEntity
        {
            Path = path,
            ProjectName = "Test Project",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        };
        Db.Workspaces.Add(ws);
        Db.SaveChanges();
        return ws;
    }

    // ── IsTerminalStatus ────────────────────────────────────────

    [Theory]
    [InlineData("Completed", true)]
    [InlineData("Archived", true)]
    [InlineData("Active", false)]
    [InlineData("Idle", false)]
    public void IsTerminalStatus_ReturnsExpected(string status, bool expected)
    {
        Assert.Equal(expected, BreakoutRoomService.IsTerminalStatus(status));
    }

    // ── CreateBreakoutRoomAsync ─────────────────────────────────

    [Fact]
    public async Task Create_ValidInputs_ReturnsBreakoutRoom()
    {
        var room = AddRoom();
        var result = await Sut.CreateBreakoutRoomAsync(room.Id, "agent-1", "Feature Work");

        Assert.NotNull(result);
        Assert.Equal("Feature Work", result.Name);
    }

    [Fact]
    public async Task Create_SetsActiveStatus()
    {
        var room = AddRoom();
        var result = await Sut.CreateBreakoutRoomAsync(room.Id, "agent-1", "Work");

        Assert.Equal(RoomStatus.Active, result.Status);
    }

    [Fact]
    public async Task Create_SetsCorrectParentRoomId()
    {
        var room = AddRoom();
        var result = await Sut.CreateBreakoutRoomAsync(room.Id, "agent-1", "Work");

        Assert.Equal(room.Id, result.ParentRoomId);
    }

    [Fact]
    public async Task Create_SetsAssignedAgentId()
    {
        var room = AddRoom();
        var result = await Sut.CreateBreakoutRoomAsync(room.Id, "agent-1", "Work");

        Assert.Equal("agent-1", result.AssignedAgentId);
    }

    [Fact]
    public async Task Create_PersistsToDatabase()
    {
        var room = AddRoom();
        var result = await Sut.CreateBreakoutRoomAsync(room.Id, "agent-1", "Work");

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal("Active", persisted.Status);
        Assert.Equal("agent-1", persisted.AssignedAgentId);
    }

    [Fact]
    public async Task Create_MovesAgentToWorkingState()
    {
        var room = AddRoom();
        await Sut.CreateBreakoutRoomAsync(room.Id, "agent-1", "Work");

        Db.ChangeTracker.Clear();
        var location = await Db.AgentLocations.FirstOrDefaultAsync(l => l.AgentId == "agent-1");
        Assert.NotNull(location);
        Assert.Equal(nameof(AgentState.Working), location.State);
    }

    [Fact]
    public async Task Create_ParentRoomNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut.CreateBreakoutRoomAsync("nonexistent", "agent-1", "Work"));
    }

    [Fact]
    public async Task Create_AgentNotInCatalog_Throws()
    {
        var room = AddRoom();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut.CreateBreakoutRoomAsync(room.Id, "unknown-agent", "Work"));
    }

    [Fact]
    public async Task Create_EmptyTasks()
    {
        var room = AddRoom();
        var result = await Sut.CreateBreakoutRoomAsync(room.Id, "agent-1", "Work");

        Assert.Empty(result.Tasks);
    }

    [Fact]
    public async Task Create_EmptyRecentMessages()
    {
        var room = AddRoom();
        var result = await Sut.CreateBreakoutRoomAsync(room.Id, "agent-1", "Work");

        Assert.Empty(result.RecentMessages);
    }

    [Fact]
    public async Task Create_PublishesActivityEvent()
    {
        var room = AddRoom();
        await Sut.CreateBreakoutRoomAsync(room.Id, "agent-1", "Work");

        var events = _graph.ActivityBus.GetRecentActivity();
        Assert.Contains(events, e => e.Type == ActivityEventType.RoomCreated);
    }

    // ── CloseBreakoutRoomAsync ──────────────────────────────────

    [Fact]
    public async Task Close_ValidBreakout_SetsArchivedStatus()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        await Sut.CloseBreakoutRoomAsync(breakout.Id);

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal("Archived", persisted!.Status);
    }

    [Fact]
    public async Task Close_SetsCloseReason()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        await Sut.CloseBreakoutRoomAsync(breakout.Id);

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal("Completed", persisted!.CloseReason);
    }

    [Fact]
    public async Task Close_MovesAgentToIdle()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        await Sut.CloseBreakoutRoomAsync(breakout.Id);

        Db.ChangeTracker.Clear();
        var location = await Db.AgentLocations.FirstOrDefaultAsync(l => l.AgentId == "agent-1");
        Assert.NotNull(location);
        Assert.Equal(nameof(AgentState.Idle), location.State);
    }

    [Fact]
    public async Task Close_AlreadyArchived_NoOp()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1", status: "Archived", closeReason: "Completed");
        var originalUpdated = breakout.UpdatedAt;

        await Sut.CloseBreakoutRoomAsync(breakout.Id);

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal("Archived", persisted!.Status);
        Assert.Equal(originalUpdated, persisted.UpdatedAt);
    }

    [Fact]
    public async Task Close_AlreadyCompleted_NoOp()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1", status: "Completed");
        var originalUpdated = breakout.UpdatedAt;

        await Sut.CloseBreakoutRoomAsync(breakout.Id);

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal("Completed", persisted!.Status);
    }

    [Fact]
    public async Task Close_NotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut.CloseBreakoutRoomAsync("nonexistent"));
    }

    [Theory]
    [InlineData(BreakoutRoomCloseReason.Recalled)]
    [InlineData(BreakoutRoomCloseReason.Cancelled)]
    [InlineData(BreakoutRoomCloseReason.StuckDetected)]
    [InlineData(BreakoutRoomCloseReason.ClosedByRecovery)]
    [InlineData(BreakoutRoomCloseReason.Failed)]
    public async Task Close_CustomCloseReason_Persisted(BreakoutRoomCloseReason reason)
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        await Sut.CloseBreakoutRoomAsync(breakout.Id, reason);

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal(reason.ToString(), persisted!.CloseReason);
    }

    [Fact]
    public async Task Close_DefaultCloseReason_IsCompleted()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        await Sut.CloseBreakoutRoomAsync(breakout.Id);

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal(nameof(BreakoutRoomCloseReason.Completed), persisted!.CloseReason);
    }

    // ── TryReopenBreakoutForTaskAsync ───────────────────────────

    [Fact]
    public async Task TryReopen_NoMatchingBreakout_DoesNothing()
    {
        // Should not throw — just silently returns
        await Sut.TryReopenBreakoutForTaskAsync("nonexistent-task", "Bad code", "Reviewer");
    }

    [Fact]
    public async Task TryReopen_ActiveBreakout_DoesNothing()
    {
        var room = AddRoom();
        var task = AddTask(room.Id, "agent-1");
        var breakout = AddBreakout(room.Id, "agent-1", status: "Active", taskId: task.Id);

        await Sut.TryReopenBreakoutForTaskAsync(task.Id, "Bad code", "Reviewer");

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal("Active", persisted!.Status);
    }

    [Fact]
    public async Task TryReopen_ArchivedBreakout_SetsActive()
    {
        var room = AddRoom();
        var task = AddTask(room.Id, "agent-1");
        var breakout = AddBreakout(room.Id, "agent-1", status: "Archived", taskId: task.Id, closeReason: "Completed");

        await Sut.TryReopenBreakoutForTaskAsync(task.Id, "Needs fixes", "Socrates");

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal("Active", persisted!.Status);
    }

    [Fact]
    public async Task TryReopen_ClearsCloseReason()
    {
        var room = AddRoom();
        var task = AddTask(room.Id, "agent-1");
        var breakout = AddBreakout(room.Id, "agent-1", status: "Archived", taskId: task.Id, closeReason: "Completed");

        await Sut.TryReopenBreakoutForTaskAsync(task.Id, "Needs fixes", "Socrates");

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Null(persisted!.CloseReason);
    }

    [Fact]
    public async Task TryReopen_MovesAgentBackToWorking()
    {
        var room = AddRoom();
        var task = AddTask(room.Id, "agent-1");
        AddBreakout(room.Id, "agent-1", status: "Archived", taskId: task.Id, closeReason: "Completed");

        await Sut.TryReopenBreakoutForTaskAsync(task.Id, "Needs fixes", "Socrates");

        Db.ChangeTracker.Clear();
        var location = await Db.AgentLocations.FirstOrDefaultAsync(l => l.AgentId == "agent-1");
        Assert.NotNull(location);
        Assert.Equal(nameof(AgentState.Working), location.State);
    }

    [Fact]
    public async Task TryReopen_AddsSystemMessage()
    {
        var room = AddRoom();
        var task = AddTask(room.Id, "agent-1");
        var breakout = AddBreakout(room.Id, "agent-1", status: "Archived", taskId: task.Id, closeReason: "Completed");

        await Sut.TryReopenBreakoutForTaskAsync(task.Id, "Missing tests", "Socrates");

        // SaveChanges is called by the caller in production, but the entity is tracked
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        var messages = await Db.BreakoutMessages
            .Where(m => m.BreakoutRoomId == breakout.Id)
            .ToListAsync();
        Assert.Single(messages);
        Assert.Contains("Socrates", messages[0].Content);
        Assert.Contains("Missing tests", messages[0].Content);
        Assert.Equal("system", messages[0].SenderId);
    }

    [Fact]
    public async Task TryReopen_MultipleArchived_OpensLatest()
    {
        var room = AddRoom();
        var task = AddTask(room.Id, "agent-1");

        // Older breakout
        var older = AddBreakout(room.Id, "agent-1", status: "Archived", taskId: task.Id, closeReason: "Completed");
        // Hack CreatedAt to be older
        older.CreatedAt = DateTime.UtcNow.AddHours(-2);
        Db.SaveChanges();

        // Newer breakout
        var newer = AddBreakout(room.Id, "agent-1", status: "Archived", taskId: task.Id, closeReason: "Completed");
        newer.CreatedAt = DateTime.UtcNow.AddHours(-1);
        Db.SaveChanges();

        await Sut.TryReopenBreakoutForTaskAsync(task.Id, "Fix it", "Socrates");

        Db.ChangeTracker.Clear();
        var reopened = await Db.BreakoutRooms.FindAsync(newer.Id);
        var stillArchived = await Db.BreakoutRooms.FindAsync(older.Id);
        Assert.Equal("Active", reopened!.Status);
        Assert.Equal("Archived", stillArchived!.Status);
    }

    // ── GetBreakoutRoomAsync ────────────────────────────────────

    [Fact]
    public async Task GetBreakout_NotFound_ReturnsNull()
    {
        var result = await Sut.GetBreakoutRoomAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetBreakout_Found_ReturnsModel()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        var result = await Sut.GetBreakoutRoomAsync(breakout.Id);

        Assert.NotNull(result);
        Assert.Equal(breakout.Id, result.Id);
        Assert.Equal(room.Id, result.ParentRoomId);
        Assert.Equal("agent-1", result.AssignedAgentId);
    }

    [Fact]
    public async Task GetBreakout_IncludesMessages()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");
        AddMessage(breakout.Id, "Test message");

        Db.ChangeTracker.Clear();
        var result = await Sut.GetBreakoutRoomAsync(breakout.Id);

        Assert.NotNull(result);
        Assert.Single(result.RecentMessages);
        Assert.Equal("Test message", result.RecentMessages[0].Content);
    }

    // ── GetBreakoutRoomsAsync ───────────────────────────────────

    [Fact]
    public async Task GetBreakoutRooms_NoRooms_ReturnsEmpty()
    {
        var room = AddRoom();
        var result = await Sut.GetBreakoutRoomsAsync(room.Id);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBreakoutRooms_ReturnsActiveOnly()
    {
        var room = AddRoom();
        AddBreakout(room.Id, "agent-1", status: "Active");
        AddBreakout(room.Id, "agent-2", status: "Archived");

        Db.ChangeTracker.Clear();
        var result = await Sut.GetBreakoutRoomsAsync(room.Id);

        Assert.Single(result);
        Assert.Equal(RoomStatus.Active, result[0].Status);
    }

    [Fact]
    public async Task GetBreakoutRooms_FiltersByParentRoom()
    {
        var room1 = AddRoom();
        var room2 = AddRoom();
        AddBreakout(room1.Id, "agent-1");
        AddBreakout(room2.Id, "agent-2");

        Db.ChangeTracker.Clear();
        var result = await Sut.GetBreakoutRoomsAsync(room1.Id);

        Assert.Single(result);
        Assert.Equal(room1.Id, result[0].ParentRoomId);
    }

    // ── GetAllBreakoutRoomsAsync ────────────────────────────────

    [Fact]
    public async Task GetAllBreakoutRooms_NoRooms_ReturnsEmpty()
    {
        var result = await Sut.GetAllBreakoutRoomsAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllBreakoutRooms_ReturnsActiveOnly()
    {
        var room = AddRoom();
        AddBreakout(room.Id, "agent-1", status: "Active");
        AddBreakout(room.Id, "agent-2", status: "Archived");

        Db.ChangeTracker.Clear();
        var result = await Sut.GetAllBreakoutRoomsAsync();

        Assert.Single(result);
    }

    [Fact]
    public async Task GetAllBreakoutRooms_AcrossMultipleParents()
    {
        var room1 = AddRoom();
        var room2 = AddRoom();
        AddBreakout(room1.Id, "agent-1");
        AddBreakout(room2.Id, "agent-2");

        Db.ChangeTracker.Clear();
        var result = await Sut.GetAllBreakoutRoomsAsync();

        Assert.Equal(2, result.Count);
    }

    // ── GetAgentSessionsAsync ───────────────────────────────────

    [Fact]
    public async Task GetAgentSessions_NoSessions_ReturnsEmpty()
    {
        var result = await Sut.GetAgentSessionsAsync("agent-1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentSessions_ReturnsAllStatuses()
    {
        var room = AddRoom();
        AddBreakout(room.Id, "agent-1", status: "Active");
        AddBreakout(room.Id, "agent-1", status: "Archived");

        Db.ChangeTracker.Clear();
        var result = await Sut.GetAgentSessionsAsync("agent-1");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetAgentSessions_OrderedByUpdatedAtDesc()
    {
        var room = AddRoom();
        var older = AddBreakout(room.Id, "agent-1", updatedAt: DateTime.UtcNow.AddHours(-2));
        var newer = AddBreakout(room.Id, "agent-1", updatedAt: DateTime.UtcNow.AddHours(-1));

        Db.ChangeTracker.Clear();
        var result = await Sut.GetAgentSessionsAsync("agent-1");

        Assert.Equal(2, result.Count);
        Assert.Equal(newer.Id, result[0].Id);
        Assert.Equal(older.Id, result[1].Id);
    }

    [Fact]
    public async Task GetAgentSessions_FiltersByAgentId()
    {
        var room = AddRoom();
        AddBreakout(room.Id, "agent-1");
        AddBreakout(room.Id, "agent-2");

        Db.ChangeTracker.Clear();
        var result = await Sut.GetAgentSessionsAsync("agent-1");

        Assert.Single(result);
        Assert.Equal("agent-1", result[0].AssignedAgentId);
    }

    [Fact]
    public async Task GetAgentSessions_ScopesToActiveWorkspace()
    {
        var wsPath = "/workspace/active";
        AddWorkspace(wsPath, isActive: true);
        var inScopeRoom = AddRoom(workspacePath: wsPath);
        var outOfScopeRoom = AddRoom(workspacePath: "/workspace/other");

        AddBreakout(inScopeRoom.Id, "agent-1");
        AddBreakout(outOfScopeRoom.Id, "agent-1");

        Db.ChangeTracker.Clear();
        var result = await Sut.GetAgentSessionsAsync("agent-1");

        Assert.Single(result);
        Assert.Equal(inScopeRoom.Id, result[0].ParentRoomId);
    }

    // ── SetBreakoutTaskIdAsync ──────────────────────────────────

    [Fact]
    public async Task SetTaskId_Valid_SetsTaskId()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");
        var task = AddTask(room.Id);

        await Sut.SetBreakoutTaskIdAsync(breakout.Id, task.Id);

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal(task.Id, persisted!.TaskId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetTaskId_EmptyBreakoutId_Throws(string? breakoutId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Sut.SetBreakoutTaskIdAsync(breakoutId!, "some-task"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetTaskId_EmptyTaskId_Throws(string? taskId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Sut.SetBreakoutTaskIdAsync("some-breakout", taskId!));
    }

    [Fact]
    public async Task SetTaskId_BreakoutNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut.SetBreakoutTaskIdAsync("nonexistent", "task-1"));
    }

    [Fact]
    public async Task SetTaskId_AlreadyLinkedToSameTask_NoOp()
    {
        var room = AddRoom();
        var task = AddTask(room.Id);
        var breakout = AddBreakout(room.Id, "agent-1", taskId: task.Id);

        // Should not throw
        await Sut.SetBreakoutTaskIdAsync(breakout.Id, task.Id);

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal(task.Id, persisted!.TaskId);
    }

    [Fact]
    public async Task SetTaskId_AlreadyLinkedToDifferentTask_Throws()
    {
        var room = AddRoom();
        var task1 = AddTask(room.Id);
        var task2 = AddTask(room.Id);
        var breakout = AddBreakout(room.Id, "agent-1", taskId: task1.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut.SetBreakoutTaskIdAsync(breakout.Id, task2.Id));
    }

    // ── GetBreakoutTaskIdAsync ──────────────────────────────────

    [Fact]
    public async Task GetTaskId_NoBreakout_ReturnsNull()
    {
        var result = await Sut.GetBreakoutTaskIdAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetTaskId_NoTaskLinked_ReturnsNull()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        var result = await Sut.GetBreakoutTaskIdAsync(breakout.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTaskId_TaskLinked_ReturnsId()
    {
        var room = AddRoom();
        var task = AddTask(room.Id);
        var breakout = AddBreakout(room.Id, "agent-1", taskId: task.Id);

        var result = await Sut.GetBreakoutTaskIdAsync(breakout.Id);

        Assert.Equal(task.Id, result);
    }

    // ── TransitionBreakoutTaskToInReviewAsync ───────────────────

    [Fact]
    public async Task TransitionToInReview_NoLinkedTask_ReturnsNull()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        var result = await Sut.TransitionBreakoutTaskToInReviewAsync(breakout.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task TransitionToInReview_WithLinkedTask_ReturnsSnapshot()
    {
        var room = AddRoom();
        var task = AddTask(room.Id, "agent-1");
        var breakout = AddBreakout(room.Id, "agent-1", taskId: task.Id);

        var result = await Sut.TransitionBreakoutTaskToInReviewAsync(breakout.Id);

        Assert.NotNull(result);
        Assert.Equal(task.Id, result.Id);
        Assert.Equal(TaskStatus.InReview, result.Status);
    }

    // ── EnsureTaskForBreakoutAsync ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnsureTask_EmptyBreakoutId_Throws(string? breakoutId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Sut.EnsureTaskForBreakoutAsync(breakoutId!, "Title", "Desc", "agent-1", "room-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnsureTask_EmptyTitle_Throws(string? title)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Sut.EnsureTaskForBreakoutAsync("breakout-1", title!, "Desc", "agent-1", "room-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnsureTask_EmptyDescription_Throws(string? description)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Sut.EnsureTaskForBreakoutAsync("breakout-1", "Title", description!, "agent-1", "room-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnsureTask_EmptyAgentId_Throws(string? agentId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Sut.EnsureTaskForBreakoutAsync("breakout-1", "Title", "Desc", agentId!, "room-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnsureTask_EmptyRoomId_Throws(string? roomId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Sut.EnsureTaskForBreakoutAsync("breakout-1", "Title", "Desc", "agent-1", roomId!));
    }

    [Fact]
    public async Task EnsureTask_BreakoutNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut.EnsureTaskForBreakoutAsync("nonexistent", "Title", "Desc", "agent-1", "room-1"));
    }

    [Fact]
    public async Task EnsureTask_WrongParentRoom_Throws()
    {
        var room1 = AddRoom();
        var room2 = AddRoom();
        var breakout = AddBreakout(room1.Id, "agent-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut.EnsureTaskForBreakoutAsync(breakout.Id, "Title", "Desc", "agent-1", room2.Id));
    }

    [Fact]
    public async Task EnsureTask_WrongAgent_Throws()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut.EnsureTaskForBreakoutAsync(breakout.Id, "Title", "Desc", "agent-2", room.Id));
    }

    [Fact]
    public async Task EnsureTask_AlreadyLinked_ReturnsExistingId()
    {
        var room = AddRoom();
        var task = AddTask(room.Id, "agent-1");
        var breakout = AddBreakout(room.Id, "agent-1", taskId: task.Id);

        var result = await Sut.EnsureTaskForBreakoutAsync(
            breakout.Id, "New Title", "New Desc", "agent-1", room.Id);

        Assert.Equal(task.Id, result);
    }

    [Fact]
    public async Task EnsureTask_NoExistingTask_CreatesNewTask()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        var taskId = await Sut.EnsureTaskForBreakoutAsync(
            breakout.Id, "Build Feature", "Implement it", "agent-1", room.Id);

        Assert.False(string.IsNullOrWhiteSpace(taskId));
        Db.ChangeTracker.Clear();
        var created = await Db.Tasks.FindAsync(taskId);
        Assert.NotNull(created);
    }

    [Fact]
    public async Task EnsureTask_NewTask_LinksToBreakout()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        var taskId = await Sut.EnsureTaskForBreakoutAsync(
            breakout.Id, "Build Feature", "Implement it", "agent-1", room.Id);

        Db.ChangeTracker.Clear();
        var persisted = await Db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.Equal(taskId, persisted!.TaskId);
    }

    [Fact]
    public async Task EnsureTask_NewTask_SetsCorrectFields()
    {
        var room = AddRoom(workspacePath: "/ws/proj");
        var breakout = AddBreakout(room.Id, "agent-1");

        var taskId = await Sut.EnsureTaskForBreakoutAsync(
            breakout.Id, "Build Feature", "Implement it", "agent-1", room.Id,
            branchName: "feat/thing");

        Db.ChangeTracker.Clear();
        var created = await Db.Tasks.FindAsync(taskId);
        Assert.NotNull(created);
        Assert.Equal("Build Feature", created.Title);
        Assert.Equal("Implement it", created.Description);
        Assert.Equal("agent-1", created.AssignedAgentId);
        Assert.Equal("Agent One", created.AssignedAgentName);
        Assert.Equal(room.Id, created.RoomId);
        Assert.Equal("/ws/proj", created.WorkspacePath);
        Assert.Equal("feat/thing", created.BranchName);
        Assert.Equal(nameof(TaskStatus.Active), created.Status);
        Assert.Equal(nameof(TaskType.Feature), created.Type);
    }

    [Fact]
    public async Task EnsureTask_ExistingTaskMissing_Throws()
    {
        var room = AddRoom();
        // Directly set a TaskId that doesn't exist in the Tasks table
        var breakout = AddBreakout(room.Id, "agent-1", taskId: "ghost-task");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut.EnsureTaskForBreakoutAsync(
                breakout.Id, "Title", "Desc", "agent-1", room.Id));
    }

    [Fact]
    public async Task EnsureTask_WithCurrentPlan_UsesIt()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        var taskId = await Sut.EnsureTaskForBreakoutAsync(
            breakout.Id, "Build Feature", "Implement it", "agent-1", room.Id,
            currentPlan: "  My custom plan  ");

        Db.ChangeTracker.Clear();
        var created = await Db.Tasks.FindAsync(taskId);
        Assert.Equal("My custom plan", created!.CurrentPlan);
    }

    [Fact]
    public async Task EnsureTask_WithoutPlan_GeneratesDefault()
    {
        var room = AddRoom();
        var breakout = AddBreakout(room.Id, "agent-1");

        var taskId = await Sut.EnsureTaskForBreakoutAsync(
            breakout.Id, "Build Feature", "Implement it", "agent-1", room.Id);

        Db.ChangeTracker.Clear();
        var created = await Db.Tasks.FindAsync(taskId);
        Assert.Contains("# Build Feature", created!.CurrentPlan);
        Assert.Contains("## Plan", created.CurrentPlan);
    }
}
