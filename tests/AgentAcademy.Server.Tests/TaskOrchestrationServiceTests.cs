using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

[Collection("WorkspaceRuntime")]
public class TaskOrchestrationServiceTests : IDisposable
{
    private readonly List<IServiceScope> _scopes = [];
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    private const string DefaultRoomId = "main-room";
    private const string DefaultRoomName = "Main Collaboration Room";
    private const string WorkspacePath = "/workspace/test";

    public TaskOrchestrationServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: DefaultRoomId,
            DefaultRoomName: DefaultRoomName,
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: ["planning"], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: ["coding"], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: ["review"], EnabledTools: [], AutoJoinDefaultRoom: false)
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<TaskQueryService>();
        services.AddScoped<AgentLocationService>();
        services.AddScoped<RoomSnapshotBuilder>();
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<RoomService>();
        services.AddScoped<MessageService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<TaskOrchestrationService>();
        services.AddSingleton<IAgentExecutor, StubExecutor>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        SeedDefaults(db);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes) scope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private (TaskOrchestrationService Svc, AgentAcademyDbContext Db) CreateScope()
    {
        var scope = _serviceProvider.CreateScope();
        _scopes.Add(scope);
        return (
            scope.ServiceProvider.GetRequiredService<TaskOrchestrationService>(),
            scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>());
    }

    private static void SeedDefaults(AgentAcademyDbContext db)
    {
        db.Workspaces.Add(new WorkspaceEntity
        {
            Path = WorkspacePath,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        db.Rooms.Add(new RoomEntity
        {
            Id = DefaultRoomId,
            Name = DefaultRoomName,
            Status = nameof(RoomStatus.Active),
            CurrentPhase = nameof(CollaborationPhase.Intake),
            WorkspacePath = WorkspacePath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    private static TaskAssignmentRequest MakeRequest(
        string title = "Implement feature X",
        string description = "Build feature X with tests",
        string successCriteria = "All tests pass",
        string? roomId = null,
        List<string>? preferredRoles = null,
        string? correlationId = null,
        string? currentPlan = null)
    {
        return new TaskAssignmentRequest(
            Title: title,
            Description: description,
            SuccessCriteria: successCriteria,
            RoomId: roomId,
            PreferredRoles: preferredRoles ?? [],
            CorrelationId: correlationId,
            CurrentPlan: currentPlan
        );
    }

    private static RoomEntity SeedRoom(
        AgentAcademyDbContext db,
        string id = "existing-room",
        string name = "Existing Room",
        string status = nameof(RoomStatus.Idle),
        string phase = nameof(CollaborationPhase.Intake),
        string? workspacePath = WorkspacePath)
    {
        var room = new RoomEntity
        {
            Id = id,
            Name = name,
            Status = status,
            CurrentPhase = phase,
            WorkspacePath = workspacePath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Rooms.Add(room);
        db.SaveChanges();
        return room;
    }

    private static TaskEntity SeedTask(
        AgentAcademyDbContext db,
        string id = "task-1",
        string status = nameof(TaskStatus.Active),
        string? roomId = DefaultRoomId,
        string? assignedAgentId = null,
        string? mergeCommitSha = null,
        int reviewRounds = 0)
    {
        var entity = new TaskEntity
        {
            Id = id,
            Title = "Test Task",
            Description = "A test task",
            SuccessCriteria = "Tests pass",
            Status = status,
            Type = "Feature",
            CurrentPhase = "Planning",
            CurrentPlan = "# Plan",
            RoomId = roomId,
            WorkspacePath = WorkspacePath,
            AssignedAgentId = assignedAgentId,
            MergeCommitSha = mergeCommitSha,
            ReviewRounds = reviewRounds,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Tasks.Add(entity);
        db.SaveChanges();
        return entity;
    }

    private static SprintEntity SeedSprint(
        AgentAcademyDbContext db,
        string? workspacePath = WorkspacePath,
        string status = "Active")
    {
        var sprint = new SprintEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Number = 1,
            WorkspacePath = workspacePath ?? WorkspacePath,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
        db.Sprints.Add(sprint);
        db.SaveChanges();
        return sprint;
    }

    private static void SeedAgentLocation(
        AgentAcademyDbContext db,
        string agentId,
        string roomId = DefaultRoomId,
        string state = nameof(AgentState.Idle))
    {
        db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = agentId,
            RoomId = roomId,
            State = state,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    // ═══════════════════════════════════════════════════════════════
    // CreateTaskAsync — New Room
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateTask_NoRoomId_CreatesNewRoom()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        Assert.NotNull(result.Room);
        Assert.NotEqual(DefaultRoomId, result.Room.Id);
        Assert.Contains("implement-feature-x", result.Room.Id);
    }

    [Fact]
    public async Task CreateTask_NoRoomId_NewRoomHasActiveStatus()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        Assert.Equal(RoomStatus.Active, result.Room.Status);
    }

    [Fact]
    public async Task CreateTask_NoRoomId_NewRoomHasPlanningPhase()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        Assert.Equal(CollaborationPhase.Planning, result.Room.CurrentPhase);
    }

    [Fact]
    public async Task CreateTask_NoRoomId_NewRoomNameMatchesTitle()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest(title: "Build Dashboard");

        var result = await svc.CreateTaskAsync(request);

        Assert.Equal("Build Dashboard", result.Room.Name);
    }

    [Fact]
    public async Task CreateTask_NoRoomId_RoomGetsActiveWorkspacePath()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync(result.Room.Id);
        Assert.NotNull(room);
        Assert.Equal(WorkspacePath, room.WorkspacePath);
    }

    [Fact]
    public async Task CreateTask_NoRoomId_CreatesTaskEntity()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest(title: "My Task", description: "My desc", successCriteria: "Pass");

        var result = await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var task = await db.Tasks.FindAsync(result.Task.Id);
        Assert.NotNull(task);
        Assert.Equal("My Task", task.Title);
        Assert.Equal("My desc", task.Description);
        Assert.Equal("Pass", task.SuccessCriteria);
        Assert.Equal(nameof(TaskStatus.Active), task.Status);
    }

    [Fact]
    public async Task CreateTask_NoRoomId_TaskHasRoomId()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var task = await db.Tasks.FindAsync(result.Task.Id);
        Assert.NotNull(task);
        Assert.Equal(result.Room.Id, task.RoomId);
    }

    [Fact]
    public async Task CreateTask_NoRoomId_ReturnsTaskSnapshot()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest(title: "Feature Z");

        var result = await svc.CreateTaskAsync(request);

        Assert.Equal("Feature Z", result.Task.Title);
        Assert.Equal(TaskStatus.Active, result.Task.Status);
        Assert.Equal(CollaborationPhase.Planning, result.Task.CurrentPhase);
    }

    [Fact]
    public async Task CreateTask_NoRoomId_ReturnsActivityEvent()
    {
        var (svc, _) = CreateScope();
        var request = MakeRequest(title: "Feature Z");

        var result = await svc.CreateTaskAsync(request);

        Assert.NotNull(result.Activity);
        Assert.Contains("Feature Z", result.Activity.Message);
    }

    [Fact]
    public async Task CreateTask_NoRoomId_UsesProvidedCorrelationId()
    {
        var (svc, _) = CreateScope();
        var request = MakeRequest(correlationId: "my-corr-id");

        var result = await svc.CreateTaskAsync(request);

        Assert.Equal("my-corr-id", result.CorrelationId);
    }

    [Fact]
    public async Task CreateTask_NoRoomId_GeneratesCorrelationIdWhenNull()
    {
        var (svc, _) = CreateScope();
        var request = MakeRequest(correlationId: null);

        var result = await svc.CreateTaskAsync(request);

        Assert.False(string.IsNullOrEmpty(result.CorrelationId));
    }

    [Fact]
    public async Task CreateTask_NoRoomId_ReturnsRoomSnapshot()
    {
        var (svc, _) = CreateScope();
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        Assert.NotNull(result.Room);
        Assert.False(string.IsNullOrEmpty(result.Room.Id));
    }

    // ═══════════════════════════════════════════════════════════════
    // CreateTaskAsync — Existing Room
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateTask_WithRoomId_ReusesExistingRoom()
    {
        var (svc, db) = CreateScope();
        var room = SeedRoom(db, id: "reuse-room", name: "Reuse Room");
        var request = MakeRequest(roomId: "reuse-room");

        var result = await svc.CreateTaskAsync(request);

        Assert.Equal("reuse-room", result.Room.Id);
    }

    [Fact]
    public async Task CreateTask_WithRoomId_SetsRoomToActive()
    {
        var (svc, db) = CreateScope();
        SeedRoom(db, id: "idle-room", status: nameof(RoomStatus.Idle));
        var request = MakeRequest(roomId: "idle-room");

        await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("idle-room");
        Assert.NotNull(room);
        Assert.Equal(nameof(RoomStatus.Active), room.Status);
    }

    [Fact]
    public async Task CreateTask_WithRoomId_SetsRoomToPlanning()
    {
        var (svc, db) = CreateScope();
        SeedRoom(db, id: "phase-room", phase: nameof(CollaborationPhase.Intake));
        var request = MakeRequest(roomId: "phase-room");

        await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("phase-room");
        Assert.NotNull(room);
        Assert.Equal(nameof(CollaborationPhase.Planning), room.CurrentPhase);
    }

    [Fact]
    public async Task CreateTask_WithRoomId_NotFound_Throws()
    {
        var (svc, _) = CreateScope();
        var request = MakeRequest(roomId: "nonexistent-room");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateTaskAsync(request));
        Assert.Contains("nonexistent-room", ex.Message);
    }

    [Fact]
    public async Task CreateTask_WithExistingRoom_DoesNotAutoJoinAgents()
    {
        var (svc, db) = CreateScope();
        SeedRoom(db, id: "existing-no-join");
        var request = MakeRequest(roomId: "existing-no-join");

        await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var locations = await db.AgentLocations
            .Where(l => l.RoomId == "existing-no-join")
            .ToListAsync();
        Assert.Empty(locations);
    }

    // ═══════════════════════════════════════════════════════════════
    // CreateTaskAsync — Auto-Join
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateTask_NewRoom_AutoJoinsAutoJoinAgents()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var locations = await db.AgentLocations
            .Where(l => l.RoomId == result.Room.Id)
            .ToListAsync();

        // planner-1 and engineer-1 have AutoJoinDefaultRoom=true
        Assert.Contains(locations, l => l.AgentId == "planner-1");
        Assert.Contains(locations, l => l.AgentId == "engineer-1");
    }

    [Fact]
    public async Task CreateTask_NewRoom_DoesNotJoinNonAutoJoinAgents()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var locations = await db.AgentLocations
            .Where(l => l.RoomId == result.Room.Id)
            .ToListAsync();

        // reviewer-1 has AutoJoinDefaultRoom=false
        Assert.DoesNotContain(locations, l => l.AgentId == "reviewer-1");
    }

    [Fact]
    public async Task CreateTask_NewRoom_SkipsWorkingAgents()
    {
        var (svc, db) = CreateScope();
        // Pre-seed engineer-1 as Working in another room
        SeedAgentLocation(db, "engineer-1", "some-other-room", nameof(AgentState.Working));
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var engineerLoc = await db.AgentLocations.FindAsync("engineer-1");
        Assert.NotNull(engineerLoc);
        // Should still be in the other room (not moved to new room)
        Assert.Equal("some-other-room", engineerLoc.RoomId);
    }

    [Fact]
    public async Task CreateTask_NewRoom_AutoJoinAgentsSentToIdleState()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var plannerLoc = await db.AgentLocations.FindAsync("planner-1");
        Assert.NotNull(plannerLoc);
        Assert.Equal(nameof(AgentState.Idle), plannerLoc.State);
    }

    // ═══════════════════════════════════════════════════════════════
    // CreateTaskAsync — Sprint Association
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateTask_AssociatesWithActiveSprint()
    {
        var (svc, db) = CreateScope();
        var sprint = SeedSprint(db);
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var task = await db.Tasks.FindAsync(result.Task.Id);
        Assert.NotNull(task);
        Assert.Equal(sprint.Id, task.SprintId);
    }

    [Fact]
    public async Task CreateTask_NoActiveSprint_TaskHasNullSprintId()
    {
        var (svc, db) = CreateScope();
        // No sprint seeded
        var request = MakeRequest();

        var result = await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var task = await db.Tasks.FindAsync(result.Task.Id);
        Assert.NotNull(task);
        Assert.Null(task.SprintId);
    }

    // ═══════════════════════════════════════════════════════════════
    // CreateTaskAsync — Messages
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateTask_CreatesAssignmentAndPlanningMessages()
    {
        var (svc, db) = CreateScope();
        var request = MakeRequest(title: "Feature Alpha");

        var result = await svc.CreateTaskAsync(request);

        db.ChangeTracker.Clear();
        var messages = await db.Messages
            .Where(m => m.RoomId == result.Room.Id)
            .OrderBy(m => m.SentAt)
            .ToListAsync();

        // Should have at least the TaskAssignment and Coordination messages
        Assert.True(messages.Count >= 2);
        Assert.Contains(messages, m => m.Kind == nameof(MessageKind.TaskAssignment));
        Assert.Contains(messages, m => m.Kind == nameof(MessageKind.Coordination));
    }

    // ═══════════════════════════════════════════════════════════════
    // CreateTaskAsync — Plan Content
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateTask_WithCurrentPlan_UsesProvidedPlan()
    {
        var (svc, _) = CreateScope();
        var request = MakeRequest(currentPlan: "# My Custom Plan\n\n- Step 1\n- Step 2");

        var result = await svc.CreateTaskAsync(request);

        Assert.Equal("# My Custom Plan\n\n- Step 1\n- Step 2", result.Task.CurrentPlan);
    }

    [Fact]
    public async Task CreateTask_NoPlan_GeneratesDefaultPlan()
    {
        var (svc, _) = CreateScope();
        var request = MakeRequest(title: "Feature X");

        var result = await svc.CreateTaskAsync(request);

        Assert.Contains("Feature X", result.Task.CurrentPlan);
        Assert.Contains("Plan", result.Task.CurrentPlan);
    }

    // ═══════════════════════════════════════════════════════════════
    // CreateTaskAsync — Preferred Roles
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateTask_WithPreferredRoles_StoresOnTask()
    {
        var (svc, _) = CreateScope();
        var request = MakeRequest(preferredRoles: ["Planner", "Reviewer"]);

        var result = await svc.CreateTaskAsync(request);

        Assert.Contains("Planner", result.Task.PreferredRoles);
        Assert.Contains("Reviewer", result.Task.PreferredRoles);
    }

    // ═══════════════════════════════════════════════════════════════
    // CompleteTaskAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompleteTask_ReturnsCompletedSnapshot()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "complete-task-1");

        var snapshot = await svc.CompleteTaskAsync("complete-task-1", commitCount: 5);

        Assert.Equal(TaskStatus.Completed, snapshot.Status);
        Assert.Equal(5, snapshot.CommitCount);
    }

    [Fact]
    public async Task CompleteTask_PersistsCompletedStatus()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "complete-task-2");

        await svc.CompleteTaskAsync("complete-task-2", commitCount: 3);

        db.ChangeTracker.Clear();
        var task = await db.Tasks.FindAsync("complete-task-2");
        Assert.NotNull(task);
        Assert.Equal(nameof(TaskStatus.Completed), task.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task CompleteTask_StoresTestsCreated()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "complete-tests-task");

        var snapshot = await svc.CompleteTaskAsync(
            "complete-tests-task", commitCount: 2,
            testsCreated: ["test1.cs", "test2.cs"]);

        Assert.Contains("test1.cs", snapshot.TestsCreated!);
        Assert.Contains("test2.cs", snapshot.TestsCreated!);
    }

    [Fact]
    public async Task CompleteTask_StoresMergeCommitSha()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "merge-task");

        var snapshot = await svc.CompleteTaskAsync(
            "merge-task", commitCount: 1, mergeCommitSha: "abc123");

        Assert.Equal("abc123", snapshot.MergeCommitSha);
    }

    [Fact]
    public async Task CompleteTask_AutoArchivesNonMainRoom()
    {
        var (svc, db) = CreateScope();
        // Use null workspace to avoid the EndsWith(StringComparison) LINQ issue in SQLite
        var room = SeedRoom(db, id: "task-room", name: "Task Room",
            status: nameof(RoomStatus.Active), workspacePath: null);
        SeedTask(db, id: "archive-task", roomId: "task-room");

        await svc.CompleteTaskAsync("archive-task", commitCount: 1);

        db.ChangeTracker.Clear();
        var updatedRoom = await db.Rooms.FindAsync("task-room");
        Assert.NotNull(updatedRoom);
        Assert.Equal(nameof(RoomStatus.Archived), updatedRoom.Status);
    }

    [Fact]
    public async Task CompleteTask_DoesNotArchiveMainRoom()
    {
        var (svc, db) = CreateScope();
        // Task is in the main room (DefaultRoomId)
        SeedTask(db, id: "main-room-task", roomId: DefaultRoomId);

        await svc.CompleteTaskAsync("main-room-task", commitCount: 1);

        db.ChangeTracker.Clear();
        var mainRoom = await db.Rooms.FindAsync(DefaultRoomId);
        Assert.NotNull(mainRoom);
        Assert.NotEqual(nameof(RoomStatus.Archived), mainRoom.Status);
    }

    [Fact]
    public async Task CompleteTask_DoesNotCrashWhenRoomIdIsNull()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "no-room-task", roomId: null);

        var snapshot = await svc.CompleteTaskAsync("no-room-task", commitCount: 0);

        Assert.Equal(TaskStatus.Completed, snapshot.Status);
    }

    [Fact]
    public async Task CompleteTask_NotFound_Throws()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CompleteTaskAsync("nonexistent", commitCount: 0));
    }

    // ═══════════════════════════════════════════════════════════════
    // RejectTaskAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RejectTask_ReturnsChangesRequestedSnapshot()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "reject-task-1", status: nameof(TaskStatus.Approved));

        var snapshot = await svc.RejectTaskAsync(
            "reject-task-1", "reviewer-1", "Code quality issues");

        Assert.Equal(TaskStatus.ChangesRequested, snapshot.Status);
    }

    [Fact]
    public async Task RejectTask_PersistsChangesRequestedStatus()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "reject-persist", status: nameof(TaskStatus.Approved));

        await svc.RejectTaskAsync("reject-persist", "reviewer-1", "Issues found");

        db.ChangeTracker.Clear();
        var task = await db.Tasks.FindAsync("reject-persist");
        Assert.NotNull(task);
        Assert.Equal(nameof(TaskStatus.ChangesRequested), task.Status);
    }

    [Fact]
    public async Task RejectTask_IncrementsReviewRounds()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "reject-rounds", status: nameof(TaskStatus.Approved), reviewRounds: 1);

        await svc.RejectTaskAsync("reject-rounds", "reviewer-1", "Needs work");

        db.ChangeTracker.Clear();
        var task = await db.Tasks.FindAsync("reject-rounds");
        Assert.NotNull(task);
        Assert.Equal(2, task.ReviewRounds);
    }

    [Fact]
    public async Task RejectTask_ReopensArchivedRoom()
    {
        var (svc, db) = CreateScope();
        var room = SeedRoom(db, id: "archived-room",
            status: nameof(RoomStatus.Archived));
        SeedTask(db, id: "reject-reopen", status: nameof(TaskStatus.Approved),
            roomId: "archived-room");

        await svc.RejectTaskAsync("reject-reopen", "reviewer-1", "Fix bugs");

        db.ChangeTracker.Clear();
        var updatedRoom = await db.Rooms.FindAsync("archived-room");
        Assert.NotNull(updatedRoom);
        Assert.Equal(nameof(RoomStatus.Active), updatedRoom.Status);
    }

    [Fact]
    public async Task RejectTask_DoesNotReopenActiveRoom()
    {
        var (svc, db) = CreateScope();
        var room = SeedRoom(db, id: "active-room-reject",
            status: nameof(RoomStatus.Active));
        SeedTask(db, id: "reject-active", status: nameof(TaskStatus.Approved),
            roomId: "active-room-reject");

        await svc.RejectTaskAsync("reject-active", "reviewer-1", "Issues");

        db.ChangeTracker.Clear();
        var updatedRoom = await db.Rooms.FindAsync("active-room-reject");
        Assert.NotNull(updatedRoom);
        // Should still be Active, not changed
        Assert.Equal(nameof(RoomStatus.Active), updatedRoom.Status);
    }

    [Fact]
    public async Task RejectTask_ReopensBreakoutForTask()
    {
        var (svc, db) = CreateScope();
        var room = SeedRoom(db, id: "br-parent-room");
        SeedTask(db, id: "reject-breakout-task",
            status: nameof(TaskStatus.Approved), roomId: "br-parent-room");
        SeedAgentLocation(db, "engineer-1", "br-parent-room", nameof(AgentState.Idle));

        // Seed an archived breakout linked to the task
        var breakout = new BreakoutRoomEntity
        {
            Id = "breakout-1",
            Name = "Engineer Breakout",
            ParentRoomId = "br-parent-room",
            AssignedAgentId = "engineer-1",
            Status = nameof(RoomStatus.Archived),
            TaskId = "reject-breakout-task",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.BreakoutRooms.Add(breakout);
        db.SaveChanges();

        await svc.RejectTaskAsync("reject-breakout-task", "reviewer-1", "Needs rework");

        db.ChangeTracker.Clear();
        var updatedBreakout = await db.BreakoutRooms.FindAsync("breakout-1");
        Assert.NotNull(updatedBreakout);
        Assert.Equal(nameof(RoomStatus.Active), updatedBreakout.Status);
    }

    [Fact]
    public async Task RejectTask_SavesChanges()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "reject-save", status: nameof(TaskStatus.Approved));

        await svc.RejectTaskAsync("reject-save", "reviewer-1", "Issues");

        // Verify using a fresh scope to confirm changes were persisted
        var (_, db2) = CreateScope();
        var task = await db2.Tasks.FindAsync("reject-save");
        Assert.NotNull(task);
        Assert.Equal(nameof(TaskStatus.ChangesRequested), task.Status);
    }

    [Fact]
    public async Task RejectTask_CompletedTask_ClearsMergeFields()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "reject-completed",
            status: nameof(TaskStatus.Completed),
            mergeCommitSha: "abc123");

        await svc.RejectTaskAsync("reject-completed", "reviewer-1", "Post-merge issue");

        db.ChangeTracker.Clear();
        var task = await db.Tasks.FindAsync("reject-completed");
        Assert.NotNull(task);
        Assert.Null(task.MergeCommitSha);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public async Task RejectTask_NotFound_Throws()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RejectTaskAsync("nonexistent", "reviewer-1", "reason"));
    }

    [Fact]
    public async Task RejectTask_PostsReviewMessageToRoom()
    {
        var (svc, db) = CreateScope();
        var room = SeedRoom(db, id: "msg-room");
        SeedTask(db, id: "reject-msg-task", status: nameof(TaskStatus.Approved),
            roomId: "msg-room");

        await svc.RejectTaskAsync("reject-msg-task", "reviewer-1", "Quality issues");

        db.ChangeTracker.Clear();
        var messages = await db.Messages
            .Where(m => m.RoomId == "msg-room" && m.Kind == nameof(MessageKind.Review))
            .ToListAsync();
        Assert.NotEmpty(messages);
        Assert.Contains(messages, m => m.Content.Contains("Rejected"));
    }

    // ═══════════════════════════════════════════════════════════════
    // PostTaskNoteAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PostTaskNote_PostsSystemStatusToRoom()
    {
        var (svc, db) = CreateScope();
        var room = SeedRoom(db, id: "note-room");
        SeedTask(db, id: "note-task", roomId: "note-room");

        await svc.PostTaskNoteAsync("note-task", "Build completed successfully");

        db.ChangeTracker.Clear();
        var messages = await db.Messages
            .Where(m => m.RoomId == "note-room")
            .ToListAsync();
        Assert.Contains(messages, m => m.Content.Contains("Build completed successfully"));
    }

    [Fact]
    public async Task PostTaskNote_TaskNotFound_Throws()
    {
        var (svc, _) = CreateScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PostTaskNoteAsync("nonexistent", "note"));
    }

    [Fact]
    public async Task PostTaskNote_TaskWithNoRoom_NoOp()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "no-room-note-task", roomId: null);

        // Should not throw
        await svc.PostTaskNoteAsync("no-room-note-task", "Some note");

        // Verify no messages were posted (no room to post to)
        db.ChangeTracker.Clear();
        var messageCount = await db.Messages.CountAsync();
        Assert.Equal(0, messageCount);
    }

    // ═══════════════════════════════════════════════════════════════
    // TryReopenRoomForTaskAsync (tested through RejectTaskAsync)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RejectTask_NullRoomId_DoesNotAttemptReopen()
    {
        var (svc, db) = CreateScope();
        SeedTask(db, id: "null-room-task", status: nameof(TaskStatus.Approved), roomId: null);

        var snapshot = await svc.RejectTaskAsync("null-room-task", "reviewer-1", "Issues");

        Assert.Equal(TaskStatus.ChangesRequested, snapshot.Status);
    }

    [Fact]
    public async Task RejectTask_IdleRoom_DoesNotReopen()
    {
        var (svc, db) = CreateScope();
        SeedRoom(db, id: "idle-reopen-room", status: nameof(RoomStatus.Idle));
        SeedTask(db, id: "reject-idle-room", status: nameof(TaskStatus.Approved),
            roomId: "idle-reopen-room");

        await svc.RejectTaskAsync("reject-idle-room", "reviewer-1", "Issues");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("idle-reopen-room");
        Assert.NotNull(room);
        // TryReopenRoomForTaskAsync only changes Archived rooms
        Assert.Equal(nameof(RoomStatus.Idle), room.Status);
    }

    [Fact]
    public async Task RejectTask_CompletedRoom_DoesNotReopen()
    {
        var (svc, db) = CreateScope();
        SeedRoom(db, id: "completed-room", status: nameof(RoomStatus.Completed));
        SeedTask(db, id: "reject-completed-room", status: nameof(TaskStatus.Approved),
            roomId: "completed-room");

        await svc.RejectTaskAsync("reject-completed-room", "reviewer-1", "Issues");

        db.ChangeTracker.Clear();
        var room = await db.Rooms.FindAsync("completed-room");
        Assert.NotNull(room);
        Assert.Equal(nameof(RoomStatus.Completed), room.Status);
    }

    // ═══════════════════════════════════════════════════════════════
    // StubExecutor (minimal IAgentExecutor for ConversationSessionService)
    // ═══════════════════════════════════════════════════════════════

    private sealed class StubExecutor : IAgentExecutor
    {
        public bool IsFullyOperational => false;
        public bool IsAuthFailed => false;
        public CircuitState CircuitBreakerState => CircuitState.Closed;

        public Task MarkAuthDegradedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAuthOperationalAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> RunAsync(AgentDefinition agent, string prompt, string? roomId,
            string? workspacePath = null, CancellationToken ct = default)
            => Task.FromResult("stub response");
        public Task InvalidateSessionAsync(string agentId, string? roomId) => Task.CompletedTask;
        public Task InvalidateRoomSessionsAsync(string roomId) => Task.CompletedTask;
        public Task InvalidateAllSessionsAsync() => Task.CompletedTask;
        public Task DisposeWorktreeClientAsync(string workspacePath) => Task.CompletedTask;
        public Task DisposeAsync() => Task.CompletedTask;
    }
}
