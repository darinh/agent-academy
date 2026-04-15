using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

public class RoomLifecycleServiceTests : IDisposable
{
    private const string DefaultRoomId = "main-room";
    private const string DefaultRoomName = "Main Collaboration Room";
    private const string WorkspacePath = "/workspace/test";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly List<IServiceScope> _scopes = [];
    private readonly AgentCatalogOptions _catalog;

    public RoomLifecycleServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: DefaultRoomId,
            DefaultRoomName: DefaultRoomName,
            Agents:
            [
                new AgentDefinition(
                    Id: "agent-1", Name: "TestAgent", Role: "Engineer",
                    Summary: "Test", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true)
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<IRoomLifecycleService>(sp => sp.GetRequiredService<RoomLifecycleService>());
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var scope in _scopes) scope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private (RoomLifecycleService Svc, AgentAcademyDbContext Db) CreateScope()
    {
        var scope = _serviceProvider.CreateScope();
        _scopes.Add(scope);
        return (
            scope.ServiceProvider.GetRequiredService<RoomLifecycleService>(),
            scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>());
    }

    private static RoomEntity MakeRoom(
        string id = "room-1",
        string name = "Breakout Room",
        string status = nameof(RoomStatus.Active),
        string? workspacePath = null)
        => new()
        {
            Id = id,
            Name = name,
            Status = status,
            WorkspacePath = workspacePath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static TaskEntity MakeTask(
        string id = "task-1",
        string roomId = "room-1",
        string status = nameof(TaskStatus.Active))
        => new()
        {
            Id = id,
            Title = "Test Task",
            Description = "Desc",
            SuccessCriteria = "OK",
            Status = status,
            RoomId = roomId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static AgentLocationEntity MakeAgentLocation(
        string agentId = "agent-1",
        string roomId = "room-1",
        string state = nameof(AgentState.InRoom))
        => new()
        {
            AgentId = agentId,
            RoomId = roomId,
            State = state,
            UpdatedAt = DateTime.UtcNow
        };

    private static WorkspaceEntity MakeWorkspace(
        string path = WorkspacePath,
        bool isActive = true)
        => new()
        {
            Path = path,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        };

    private void SeedDefaultRoom(AgentAcademyDbContext db, string? workspacePath = null)
    {
        db.Rooms.Add(MakeRoom(DefaultRoomId, DefaultRoomName, workspacePath: workspacePath));
        db.SaveChanges();
    }

    // ═══════════════════════════════════════════════════════════════
    //  IsMainCollaborationRoomAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task IsMainRoom_ReturnsFalse_WhenRoomNotFound()
    {
        var (svc, _) = CreateScope();

        var result = await svc.IsMainCollaborationRoomAsync("nonexistent");

        Assert.False(result);
    }

    [Fact]
    public async Task IsMainRoom_ReturnsTrue_ForDefaultRoomId()
    {
        var (svc, db) = CreateScope();
        SeedDefaultRoom(db);

        var result = await svc.IsMainCollaborationRoomAsync(DefaultRoomId);

        Assert.True(result);
    }

    [Fact]
    public async Task IsMainRoom_ReturnsTrue_WhenNameMatchesDefaultRoomName_InActiveWorkspace()
    {
        var (svc, db) = CreateScope();
        db.Workspaces.Add(MakeWorkspace());
        db.Rooms.Add(MakeRoom("ws-main", DefaultRoomName, workspacePath: WorkspacePath));
        db.SaveChanges();

        var result = await svc.IsMainCollaborationRoomAsync("ws-main");

        Assert.True(result);
    }

    [Fact]
    public async Task IsMainRoom_ReturnsTrue_WhenNameEndsWithMainRoom_InActiveWorkspace()
    {
        var (svc, db) = CreateScope();
        db.Workspaces.Add(MakeWorkspace());
        db.Rooms.Add(MakeRoom("ws-main2", "Sprint Main Room", workspacePath: WorkspacePath));
        db.SaveChanges();

        var result = await svc.IsMainCollaborationRoomAsync("ws-main2");

        Assert.True(result);
    }

    [Fact]
    public async Task IsMainRoom_ReturnsTrue_WhenNameEndsWithCollaborationRoom_InActiveWorkspace()
    {
        var (svc, db) = CreateScope();
        db.Workspaces.Add(MakeWorkspace());
        db.Rooms.Add(MakeRoom("ws-collab", "Team Collaboration Room", workspacePath: WorkspacePath));
        db.SaveChanges();

        var result = await svc.IsMainCollaborationRoomAsync("ws-collab");

        Assert.True(result);
    }

    [Fact]
    public async Task IsMainRoom_ReturnsFalse_ForNonMainRoomInActiveWorkspace()
    {
        var (svc, db) = CreateScope();
        db.Workspaces.Add(MakeWorkspace());
        db.Rooms.Add(MakeRoom("breakout-1", "Feature Work", workspacePath: WorkspacePath));
        db.SaveChanges();

        var result = await svc.IsMainCollaborationRoomAsync("breakout-1");

        Assert.False(result);
    }

    [Fact]
    public async Task IsMainRoom_ReturnsFalse_WhenRoomInDifferentWorkspace()
    {
        var (svc, db) = CreateScope();
        db.Workspaces.Add(MakeWorkspace("/workspace/other"));
        db.Rooms.Add(MakeRoom("ws-main", DefaultRoomName, workspacePath: WorkspacePath));
        db.SaveChanges();

        // Active workspace is /workspace/other, room is in WorkspacePath
        var result = await svc.IsMainCollaborationRoomAsync("ws-main");

        Assert.False(result);
    }

    [Fact]
    public async Task IsMainRoom_ReturnsFalse_WhenNoActiveWorkspace()
    {
        var (svc, db) = CreateScope();
        // No workspace seeded, room has workspace path but no active workspace exists
        db.Rooms.Add(MakeRoom("ws-main", DefaultRoomName, workspacePath: WorkspacePath));
        db.SaveChanges();

        var result = await svc.IsMainCollaborationRoomAsync("ws-main");

        Assert.False(result);
    }

    [Fact]
    public async Task IsMainRoom_ReturnsFalse_WhenWorkspaceIsInactive()
    {
        var (svc, db) = CreateScope();
        db.Workspaces.Add(MakeWorkspace(isActive: false));
        db.Rooms.Add(MakeRoom("ws-main", DefaultRoomName, workspacePath: WorkspacePath));
        db.SaveChanges();

        var result = await svc.IsMainCollaborationRoomAsync("ws-main");

        Assert.False(result);
    }

    [Fact]
    public async Task IsMainRoom_ReturnsFalse_WhenRoomHasNoWorkspacePath()
    {
        var (svc, db) = CreateScope();
        db.Workspaces.Add(MakeWorkspace());
        // Room has matching name but null workspace
        db.Rooms.Add(MakeRoom("orphan", DefaultRoomName, workspacePath: null));
        db.SaveChanges();

        var result = await svc.IsMainCollaborationRoomAsync("orphan");

        Assert.False(result);
    }

    [Fact]
    public async Task IsMainRoom_NameMatch_IsCaseSensitive()
    {
        var (svc, db) = CreateScope();
        db.Workspaces.Add(MakeWorkspace());
        db.Rooms.Add(MakeRoom("ws-lower", "sprint main room", workspacePath: WorkspacePath));
        db.SaveChanges();

        // "main room" lowercase should NOT match "Main Room" (ordinal comparison)
        var result = await svc.IsMainCollaborationRoomAsync("ws-lower");

        Assert.False(result);
    }

    // ═══════════════════════════════════════════════════════════════
    //  CloseRoomAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CloseRoom_Throws_WhenRoomNotFound()
    {
        var (svc, _) = CreateScope();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CloseRoomAsync("nonexistent"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task CloseRoom_Throws_WhenMainRoom()
    {
        var (svc, db) = CreateScope();
        SeedDefaultRoom(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CloseRoomAsync(DefaultRoomId));
        Assert.Contains("main collaboration room", ex.Message);
    }

    [Fact]
    public async Task CloseRoom_NoOp_WhenAlreadyArchived()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom(status: nameof(RoomStatus.Archived)));
        db.SaveChanges();

        await svc.CloseRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task CloseRoom_Throws_WhenHasParticipants()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.AgentLocations.Add(MakeAgentLocation());
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CloseRoomAsync("room-1"));
        Assert.Contains("active participant", ex.Message);
    }

    [Fact]
    public async Task CloseRoom_ArchivesRoom_WhenNoParticipants()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.SaveChanges();

        await svc.CloseRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task CloseRoom_UpdatesTimestamp()
    {
        var (svc, db) = CreateScope();
        var before = DateTime.UtcNow.AddMinutes(-5);
        var room = MakeRoom();
        room.UpdatedAt = before;
        db.Rooms.Add(room);
        db.SaveChanges();

        await svc.CloseRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var updated = await db.Rooms.FindAsync("room-1");
        Assert.True(updated!.UpdatedAt > before);
    }

    [Fact]
    public async Task CloseRoom_EmitsActivityEvent()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.SaveChanges();

        await svc.CloseRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var evt = await db.ActivityEvents
            .FirstOrDefaultAsync(e => e.Type == nameof(ActivityEventType.RoomClosed) && e.RoomId == "room-1");
        Assert.NotNull(evt);
        Assert.Contains("archived", evt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CloseRoom_IgnoresBreakoutRoomParticipants()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        // Agent is in a breakout room within this room, not the room itself
        db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = "agent-1",
            RoomId = "room-1",
            BreakoutRoomId = "breakout-sub-1",
            State = nameof(AgentState.Working),
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        // Should NOT throw — breakout participants are excluded from the count
        await svc.CloseRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task CloseRoom_Throws_WhenMultipleParticipants()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.AgentLocations.Add(MakeAgentLocation("a1"));
        db.AgentLocations.Add(MakeAgentLocation("a2"));
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CloseRoomAsync("room-1"));
        Assert.Contains("2 active participant", ex.Message);
    }

    [Fact]
    public async Task CloseRoom_Succeeds_ForIdleRoom()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom(status: nameof(RoomStatus.Idle)));
        db.SaveChanges();

        await svc.CloseRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task CloseRoom_Succeeds_ForCompletedRoom()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom(status: nameof(RoomStatus.Completed)));
        db.SaveChanges();

        await svc.CloseRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ReopenRoomAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReopenRoom_Throws_WhenRoomNotFound()
    {
        var (svc, _) = CreateScope();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReopenRoomAsync("nonexistent"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ReopenRoom_Throws_WhenNotArchived()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom(status: nameof(RoomStatus.Active)));
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReopenRoomAsync("room-1"));
        Assert.Contains("not archived", ex.Message);
    }

    [Fact]
    public async Task ReopenRoom_Throws_WhenIdle()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom(status: nameof(RoomStatus.Idle)));
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReopenRoomAsync("room-1"));
        Assert.Contains("not archived", ex.Message);
    }

    [Fact]
    public async Task ReopenRoom_SetsStatusToIdle()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom(status: nameof(RoomStatus.Archived)));
        db.SaveChanges();

        await svc.ReopenRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Idle), room!.Status);
    }

    [Fact]
    public async Task ReopenRoom_UpdatesTimestamp()
    {
        var (svc, db) = CreateScope();
        var before = DateTime.UtcNow.AddMinutes(-5);
        var room = MakeRoom(status: nameof(RoomStatus.Archived));
        room.UpdatedAt = before;
        db.Rooms.Add(room);
        db.SaveChanges();

        await svc.ReopenRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var updated = await db.Rooms.FindAsync("room-1");
        Assert.True(updated!.UpdatedAt > before);
    }

    [Fact]
    public async Task ReopenRoom_EmitsActivityEvent()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom(status: nameof(RoomStatus.Archived)));
        db.SaveChanges();

        await svc.ReopenRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var evt = await db.ActivityEvents
            .FirstOrDefaultAsync(e => e.Type == nameof(ActivityEventType.RoomStatusChanged) && e.RoomId == "room-1");
        Assert.NotNull(evt);
        Assert.Contains("reopened", evt.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════
    //  TryAutoArchiveRoomAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryAutoArchive_SkipsMainRoom()
    {
        var (svc, db) = CreateScope();
        SeedDefaultRoom(db);
        db.Tasks.Add(MakeTask(roomId: DefaultRoomId, status: nameof(TaskStatus.Completed)));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync(DefaultRoomId);

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync(DefaultRoomId);
        Assert.NotEqual(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task TryAutoArchive_SkipsWhenRoomNotFound()
    {
        var (svc, _) = CreateScope();

        // Should not throw
        await svc.TryAutoArchiveRoomAsync("nonexistent");
    }

    [Fact]
    public async Task TryAutoArchive_SkipsAlreadyArchived()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom(status: nameof(RoomStatus.Archived)));
        db.Tasks.Add(MakeTask(status: nameof(TaskStatus.Completed)));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task TryAutoArchive_SkipsWhenHasNonTerminalTasks()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask("t1", status: nameof(TaskStatus.Completed)));
        db.Tasks.Add(MakeTask("t2", status: nameof(TaskStatus.Active)));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Active), room!.Status);
    }

    [Fact]
    public async Task TryAutoArchive_SkipsWhenNoTasks()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Active), room!.Status);
    }

    [Fact]
    public async Task TryAutoArchive_ArchivesWhenAllTasksCompleted()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask("t1", status: nameof(TaskStatus.Completed)));
        db.Tasks.Add(MakeTask("t2", status: nameof(TaskStatus.Completed)));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task TryAutoArchive_ArchivesWhenAllTasksCancelled()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask("t1", status: nameof(TaskStatus.Cancelled)));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task TryAutoArchive_ArchivesWhenMixedTerminalStatuses()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask("t1", status: nameof(TaskStatus.Completed)));
        db.Tasks.Add(MakeTask("t2", status: nameof(TaskStatus.Cancelled)));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task TryAutoArchive_EvacuatesAgentsToDefaultRoom()
    {
        var (svc, db) = CreateScope();
        SeedDefaultRoom(db);
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask(status: nameof(TaskStatus.Completed)));
        db.AgentLocations.Add(MakeAgentLocation());
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var loc = await db.AgentLocations.FindAsync("agent-1");
        Assert.Equal(DefaultRoomId, loc!.RoomId);
        Assert.Equal(nameof(AgentState.Idle), loc.State);
        Assert.Null(loc.BreakoutRoomId);
    }

    [Fact]
    public async Task TryAutoArchive_EvacuatesMultipleAgents()
    {
        var (svc, db) = CreateScope();
        SeedDefaultRoom(db);
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask(status: nameof(TaskStatus.Completed)));
        db.AgentLocations.Add(MakeAgentLocation("a1"));
        db.AgentLocations.Add(MakeAgentLocation("a2"));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var loc1 = await db.AgentLocations.FindAsync("a1");
        var loc2 = await db.AgentLocations.FindAsync("a2");
        Assert.Equal(DefaultRoomId, loc1!.RoomId);
        Assert.Equal(DefaultRoomId, loc2!.RoomId);
    }

    [Fact]
    public async Task TryAutoArchive_EvacuatesToCatalogDefault_WhenRoomHasNoWorkspace()
    {
        // Note: workspace-specific evacuation uses EndsWith(StringComparison.Ordinal)
        // which SQLite can't translate. Test the fallback path instead.
        var (svc, db) = CreateScope();
        SeedDefaultRoom(db);
        db.Rooms.Add(MakeRoom("breakout-no-ws", "Feature Work", workspacePath: null));
        db.Tasks.Add(MakeTask(roomId: "breakout-no-ws", status: nameof(TaskStatus.Completed)));
        db.AgentLocations.Add(MakeAgentLocation(roomId: "breakout-no-ws"));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("breakout-no-ws");

        db.ChangeTracker.Clear();
        var loc = await db.AgentLocations.FindAsync("agent-1");
        Assert.Equal(DefaultRoomId, loc!.RoomId);
    }

    [Fact]
    public async Task TryAutoArchive_EmitsActivityEvent()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask(status: nameof(TaskStatus.Completed)));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var evt = await db.ActivityEvents
            .FirstOrDefaultAsync(e => e.Type == nameof(ActivityEventType.RoomClosed) && e.RoomId == "room-1");
        Assert.NotNull(evt);
        Assert.Contains("auto-archived", evt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryAutoArchive_DoesNotArchiveWhenTaskIsQueued()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask(status: nameof(TaskStatus.Queued)));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Active), room!.Status);
    }

    [Fact]
    public async Task TryAutoArchive_DoesNotArchiveWhenTaskIsInReview()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask(status: nameof(TaskStatus.InReview)));
        db.SaveChanges();

        await svc.TryAutoArchiveRoomAsync("room-1");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Active), room!.Status);
    }

    // ═══════════════════════════════════════════════════════════════
    //  CleanupStaleRoomsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CleanupStale_ReturnsZero_WhenNoCandidates()
    {
        var (svc, _) = CreateScope();

        var count = await svc.CleanupStaleRoomsAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CleanupStale_ReturnsZero_WhenAllRoomsArchived()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom(status: nameof(RoomStatus.Archived)));
        db.SaveChanges();

        var count = await svc.CleanupStaleRoomsAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CleanupStale_SkipsMainRoom()
    {
        var (svc, db) = CreateScope();
        SeedDefaultRoom(db);
        db.Tasks.Add(MakeTask(roomId: DefaultRoomId, status: nameof(TaskStatus.Completed)));
        db.SaveChanges();

        var count = await svc.CleanupStaleRoomsAsync();

        Assert.Equal(0, count);

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync(DefaultRoomId);
        Assert.NotEqual(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task CleanupStale_SkipsRoomWithNoTasks()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.SaveChanges();

        var count = await svc.CleanupStaleRoomsAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CleanupStale_SkipsRoomWithActiveTasks()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask(status: nameof(TaskStatus.Active)));
        db.SaveChanges();

        var count = await svc.CleanupStaleRoomsAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CleanupStale_ArchivesStaleRoom()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask(status: nameof(TaskStatus.Completed)));
        db.SaveChanges();

        var count = await svc.CleanupStaleRoomsAsync();

        Assert.Equal(1, count);

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task CleanupStale_ArchivesMultipleStaleRooms()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom("r1", "Room 1"));
        db.Rooms.Add(MakeRoom("r2", "Room 2"));
        db.Tasks.Add(MakeTask("t1", "r1", nameof(TaskStatus.Completed)));
        db.Tasks.Add(MakeTask("t2", "r2", nameof(TaskStatus.Cancelled)));
        db.SaveChanges();

        var count = await svc.CleanupStaleRoomsAsync();

        Assert.Equal(2, count);

        db.ChangeTracker.Clear();
        var r1 = await db.Rooms.FindAsync("r1");
        var r2 = await db.Rooms.FindAsync("r2");
        Assert.Equal(nameof(RoomStatus.Archived), r1!.Status);
        Assert.Equal(nameof(RoomStatus.Archived), r2!.Status);
    }

    [Fact]
    public async Task CleanupStale_MixedRooms_OnlyArchivesStale()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom("stale", "Stale Room"));
        db.Rooms.Add(MakeRoom("active", "Active Room"));
        db.Tasks.Add(MakeTask("t1", "stale", nameof(TaskStatus.Completed)));
        db.Tasks.Add(MakeTask("t2", "active", nameof(TaskStatus.Active)));
        db.SaveChanges();

        var count = await svc.CleanupStaleRoomsAsync();

        Assert.Equal(1, count);

        db.ChangeTracker.Clear();
        var stale = await db.Rooms.FindAsync("stale");
        var active = await db.Rooms.FindAsync("active");
        Assert.Equal(nameof(RoomStatus.Archived), stale!.Status);
        Assert.Equal(nameof(RoomStatus.Active), active!.Status);
    }

    [Fact]
    public async Task CleanupStale_EvacuatesAgentsFromStaleRooms()
    {
        var (svc, db) = CreateScope();
        SeedDefaultRoom(db);
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask(status: nameof(TaskStatus.Completed)));
        db.AgentLocations.Add(MakeAgentLocation());
        db.SaveChanges();

        await svc.CleanupStaleRoomsAsync();

        db.ChangeTracker.Clear();
        var loc = await db.AgentLocations.FindAsync("agent-1");
        Assert.Equal(DefaultRoomId, loc!.RoomId);
        Assert.Equal(nameof(AgentState.Idle), loc.State);
    }

    [Fact]
    public async Task CleanupStale_EmitsActivityEvents()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom("r1", "Stale 1"));
        db.Rooms.Add(MakeRoom("r2", "Stale 2"));
        db.Tasks.Add(MakeTask("t1", "r1", nameof(TaskStatus.Completed)));
        db.Tasks.Add(MakeTask("t2", "r2", nameof(TaskStatus.Completed)));
        db.SaveChanges();

        await svc.CleanupStaleRoomsAsync();

        db.ChangeTracker.Clear();
        var events = await db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.RoomClosed))
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Contains("stale", e.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CleanupStale_HandlesCompletedStatusRooms()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom(status: nameof(RoomStatus.Completed)));
        db.Tasks.Add(MakeTask(status: nameof(TaskStatus.Completed)));
        db.SaveChanges();

        var count = await svc.CleanupStaleRoomsAsync();

        Assert.Equal(1, count);

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("room-1");
        Assert.Equal(nameof(RoomStatus.Archived), room!.Status);
    }

    [Fact]
    public async Task CleanupStale_PartialTerminal_DoesNotArchive()
    {
        var (svc, db) = CreateScope();
        db.Rooms.Add(MakeRoom());
        db.Tasks.Add(MakeTask("t1", status: nameof(TaskStatus.Completed)));
        db.Tasks.Add(MakeTask("t2", status: nameof(TaskStatus.Blocked)));
        db.SaveChanges();

        var count = await svc.CleanupStaleRoomsAsync();

        Assert.Equal(0, count);
    }
}
