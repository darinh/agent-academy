using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for WorkspaceRuntime — the central state manager.
/// Uses in-memory SQLite for isolation.
/// </summary>
[Collection("WorkspaceRuntime")]
public class WorkspaceRuntimeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly WorkspaceRuntime _runtime;
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityBroadcaster _activityBus;
    private readonly ActivityPublisher _activityPublisher;

    public WorkspaceRuntimeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1",
                    Name: "Aristotle",
                    Role: "Planner",
                    Summary: "Planning lead",
                    StartupPrompt: "You are the planner.",
                    Model: null,
                    CapabilityTags: ["planning"],
                    EnabledTools: ["chat"],
                    AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "engineer-1",
                    Name: "Hephaestus",
                    Role: "SoftwareEngineer",
                    Summary: "Backend engineer",
                    StartupPrompt: "You are the engineer.",
                    Model: null,
                    CapabilityTags: ["implementation"],
                    EnabledTools: ["chat", "code"],
                    AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "reviewer-1",
                    Name: "Socrates",
                    Role: "Reviewer",
                    Summary: "Reviewer",
                    StartupPrompt: "You are the reviewer.",
                    Model: null,
                    CapabilityTags: ["review"],
                    EnabledTools: ["chat"],
                    AutoJoinDefaultRoom: false)
            ]
        );

        var logger = Substitute.For<ILogger<WorkspaceRuntime>>();
        _activityBus = new ActivityBroadcaster();
        _activityPublisher = new ActivityPublisher(_db, _activityBus);
        var settingsService = new SystemSettingsService(_db);
        var executor = Substitute.For<IAgentExecutor>();
        var sessionLogger = Substitute.For<ILogger<ConversationSessionService>>();
        var sessionService = new ConversationSessionService(_db, settingsService, executor, sessionLogger);
        var taskQueries = new TaskQueryService(_db, NullLogger<TaskQueryService>.Instance, _catalog);
        var taskLifecycle = new TaskLifecycleService(_db, NullLogger<TaskLifecycleService>.Instance, _catalog, _activityPublisher);
        var agentLocations = new AgentLocationService(_db, _catalog, _activityPublisher);
        _runtime = new WorkspaceRuntime(_db, logger, _catalog, _activityPublisher, sessionService, taskQueries, taskLifecycle,
            new MessageService(_db, NullLogger<MessageService>.Instance, _catalog, _activityPublisher, sessionService),
            new BreakoutRoomService(_db, NullLogger<BreakoutRoomService>.Instance, _catalog, _activityPublisher, sessionService, taskQueries, agentLocations),
            new TaskItemService(_db, NullLogger<TaskItemService>.Instance),
            new RoomService(_db, NullLogger<RoomService>.Instance, _catalog, _activityPublisher, sessionService,
                new MessageService(_db, NullLogger<MessageService>.Instance, _catalog, _activityPublisher, sessionService)),
            agentLocations);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Initialization ──────────────────────────────────────────

    [Fact]
    public async Task Initialize_CreatesDefaultRoom()
    {
        await _runtime.InitializeAsync();

        var room = await _runtime.GetRoomAsync("main");
        Assert.NotNull(room);
        Assert.Equal("Main Collaboration Room", room.Name);
        Assert.Equal(RoomStatus.Idle, room.Status);
        Assert.Equal(CollaborationPhase.Intake, room.CurrentPhase);
    }

    [Fact]
    public async Task Initialize_CreatesAgentLocations()
    {
        await _runtime.InitializeAsync();

        var locations = await _runtime.GetAgentLocationsAsync();
        Assert.Equal(3, locations.Count);
        Assert.All(locations, loc =>
        {
            Assert.Equal("main", loc.RoomId);
            Assert.Equal(AgentState.Idle, loc.State);
        });
    }

    [Fact]
    public async Task Initialize_IsIdempotent()
    {
        await _runtime.InitializeAsync();
        await _runtime.InitializeAsync();

        var rooms = await _runtime.GetRoomsAsync();
        Assert.Single(rooms);
    }

    [Fact]
    public async Task Initialize_AddsWelcomeMessage()
    {
        await _runtime.InitializeAsync();

        var room = await _runtime.GetRoomAsync("main");
        Assert.NotNull(room);
        Assert.NotEmpty(room.RecentMessages);
        Assert.Contains(room.RecentMessages,
            m => m.Content.Contains("Collaboration host started"));
    }

    // ── Room Management ─────────────────────────────────────────

    [Fact]
    public async Task GetRooms_ReturnsAllRooms()
    {
        await _runtime.InitializeAsync();

        var rooms = await _runtime.GetRoomsAsync();
        Assert.Single(rooms);
        Assert.Equal("main", rooms[0].Id);
    }

    [Fact]
    public async Task GetRooms_DefaultRoomAlwaysFirst()
    {
        await _runtime.InitializeAsync();

        // Create a task room whose name sorts alphabetically before "Main"
        var task = new TaskAssignmentRequest(
            Title: "Alpha Feature",
            Description: "A task that creates a room named before Main alphabetically",
            SuccessCriteria: "Room exists",
            RoomId: null,
            PreferredRoles: []);
        await _runtime.CreateTaskAsync(task);

        var rooms = await _runtime.GetRoomsAsync();
        Assert.True(rooms.Count >= 2);
        // The default main room must be first regardless of alphabetical order
        Assert.Contains("Main", rooms[0].Name);
    }

    [Fact]
    public async Task GetRooms_WorkspaceScopedDefaultRoomFirst()
    {
        // Create a workspace and its default room
        var workspacePath = "/home/test/my-project";
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = workspacePath,
            ProjectName = "My Project",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var defaultRoomId = await _runtime.EnsureDefaultRoomForWorkspaceAsync(workspacePath);

        // Create a task room in the same workspace that alphabetically sorts before main
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "aaa-first-alphabetically",
            Name = "AAA Earlier Room",
            Status = "Active",
            CurrentPhase = "Planning",
            WorkspacePath = workspacePath,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var rooms = await _runtime.GetRoomsAsync();
        Assert.True(rooms.Count >= 2);
        Assert.Equal(defaultRoomId, rooms[0].Id);
        Assert.Contains("Main", rooms[0].Name);
    }

    [Fact]
    public async Task GetRooms_FilteredByActiveWorkspace()
    {
        await _runtime.InitializeAsync();

        // Create two workspaces
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/home/test/project-a",
            ProjectName = "Project A",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        await _runtime.EnsureDefaultRoomForWorkspaceAsync("/home/test/project-a");

        // Add a room for a different workspace
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "other-project-room",
            Name = "Other Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            WorkspacePath = "/home/test/project-b",
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var rooms = await _runtime.GetRoomsAsync();
        // Should see Project A's room AND legacy rooms (null workspace), but not Project B's
        Assert.All(rooms, r =>
        {
            var wp = _db.Rooms.Find(r.Id)?.WorkspacePath;
            Assert.True(wp == "/home/test/project-a" || wp == null,
                $"Room {r.Id} has unexpected WorkspacePath: {wp}");
        });
        Assert.DoesNotContain(rooms, r => r.Id == "other-project-room");
    }

    [Fact]
    public async Task GetRoom_ReturnsNullForMissing()
    {
        var room = await _runtime.GetRoomAsync("nonexistent");
        Assert.Null(room);
    }

    [Fact]
    public async Task GetRoom_IncludesParticipants()
    {
        await _runtime.InitializeAsync();

        var room = await _runtime.GetRoomAsync("main");
        Assert.NotNull(room);
        // All agents get location entries in the default room at init
        Assert.Equal(3, room.Participants.Count);
        Assert.Contains(room.Participants, p => p.AgentId == "planner-1");
        Assert.Contains(room.Participants, p => p.AgentId == "engineer-1");
        Assert.Contains(room.Participants, p => p.AgentId == "reviewer-1");
    }

    [Fact]
    public async Task GetRoom_ParticipantsReflectAgentLocations()
    {
        await _runtime.InitializeAsync();

        // Create a task room — AutoJoinDefaultRoom agents get moved there
        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            Title: "Test Task",
            Description: "Testing participant locations",
            SuccessCriteria: "Participants reflect actual locations",
            RoomId: null,
            PreferredRoles: ["Planner"]
        ));

        // Task room should have the moved agents
        var taskRoom = await _runtime.GetRoomAsync(result.Room.Id);
        Assert.NotNull(taskRoom);
        Assert.Contains(taskRoom.Participants, p => p.AgentId == "planner-1");
        Assert.Contains(taskRoom.Participants, p => p.AgentId == "engineer-1");

        // Default room should only have agents NOT moved (reviewer-1 has AutoJoinDefaultRoom=false)
        var mainRoom = await _runtime.GetRoomAsync("main");
        Assert.NotNull(mainRoom);
        Assert.Contains(mainRoom.Participants, p => p.AgentId == "reviewer-1");
        Assert.DoesNotContain(mainRoom.Participants, p => p.AgentId == "planner-1");
        Assert.DoesNotContain(mainRoom.Participants, p => p.AgentId == "engineer-1");
    }

    // ── Message Management ──────────────────────────────────────

    [Fact]
    public async Task PostMessage_CreatesAgentMessage()
    {
        await _runtime.InitializeAsync();

        var request = new PostMessageRequest(
            RoomId: "main",
            SenderId: "planner-1",
            Content: "Let's start planning."
        );

        var envelope = await _runtime.PostMessageAsync(request);

        Assert.Equal("main", envelope.RoomId);
        Assert.Equal("planner-1", envelope.SenderId);
        Assert.Equal("Aristotle", envelope.SenderName);
        Assert.Equal(MessageSenderKind.Agent, envelope.SenderKind);
        Assert.Equal("Let's start planning.", envelope.Content);
    }

    [Fact]
    public async Task PostMessage_ThrowsForUnknownAgent()
    {
        await _runtime.InitializeAsync();

        var request = new PostMessageRequest(
            RoomId: "main",
            SenderId: "unknown-agent",
            Content: "Hello"
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _runtime.PostMessageAsync(request));
    }

    [Fact]
    public async Task PostMessage_ThrowsForMissingRoom()
    {
        var request = new PostMessageRequest(
            RoomId: "nonexistent",
            SenderId: "planner-1",
            Content: "Hello"
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _runtime.PostMessageAsync(request));
    }

    [Fact]
    public async Task PostHumanMessage_CreatesUserMessage()
    {
        await _runtime.InitializeAsync();

        var envelope = await _runtime.PostHumanMessageAsync("main", "Hello team!");

        Assert.Equal("human", envelope.SenderId);
        Assert.Equal("Human", envelope.SenderName);
        Assert.Equal(MessageSenderKind.User, envelope.SenderKind);
        Assert.Equal(MessageKind.Response, envelope.Kind);
    }

    [Fact]
    public async Task PostHumanMessage_UsesGitHubIdentity_WhenProvided()
    {
        await _runtime.InitializeAsync();

        var envelope = await _runtime.PostHumanMessageAsync("main", "Hello team!", "darinious", "Darin");

        Assert.Equal("darinious", envelope.SenderId);
        Assert.Equal("Darin", envelope.SenderName);
        Assert.Equal("Human", envelope.SenderRole);
        Assert.Equal(MessageSenderKind.User, envelope.SenderKind);
    }

    [Fact]
    public async Task PostMessage_TrimsToMaxMessages()
    {
        await _runtime.InitializeAsync();

        // Post 210 messages (exceeds the 200 limit)
        for (int i = 0; i < 210; i++)
        {
            await _runtime.PostHumanMessageAsync("main", $"Message {i}");
        }

        var room = await _runtime.GetRoomAsync("main");
        Assert.NotNull(room);
        // Should have at most 200 messages
        Assert.True(room.RecentMessages.Count <= 200);
    }

    // ── Task Management ─────────────────────────────────────────

    [Fact]
    public async Task CreateTask_CreatesTaskAndRoom()
    {
        await _runtime.InitializeAsync();

        var request = new TaskAssignmentRequest(
            Title: "Build auth system",
            Description: "Implement JWT authentication",
            SuccessCriteria: "Users can log in and out",
            RoomId: null,
            PreferredRoles: ["SoftwareEngineer"],
            CorrelationId: "test-correlation"
        );

        var result = await _runtime.CreateTaskAsync(request);

        Assert.Equal("test-correlation", result.CorrelationId);
        Assert.NotNull(result.Task);
        Assert.Equal("Build auth system", result.Task.Title);
        Assert.Equal(Shared.Models.TaskStatus.Active, result.Task.Status);
        Assert.Equal(CollaborationPhase.Planning, result.Task.CurrentPhase);

        // Room should be created
        Assert.NotNull(result.Room);
        Assert.Equal(RoomStatus.Active, result.Room.Status);
        Assert.Equal(CollaborationPhase.Planning, result.Room.CurrentPhase);
    }

    [Fact]
    public async Task CreateTask_InExistingRoom()
    {
        await _runtime.InitializeAsync();

        var request = new TaskAssignmentRequest(
            Title: "Fix bug",
            Description: "Fix the login bug",
            SuccessCriteria: "Login works",
            RoomId: "main",
            PreferredRoles: []
        );

        var result = await _runtime.CreateTaskAsync(request);

        Assert.Equal("main", result.Room.Id);
        Assert.Equal(RoomStatus.Active, result.Room.Status);
    }

    [Fact]
    public async Task CreateTask_UsesExplicitCurrentPlan()
    {
        await _runtime.InitializeAsync();

        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            Title: "Planful Task",
            Description: "Task with explicit plan",
            SuccessCriteria: "Plan persists",
            RoomId: "main",
            PreferredRoles: [],
            CurrentPlan: "# Custom Plan\n\n- Investigate\n- Implement"
        ));

        Assert.Equal("# Custom Plan\n\n- Investigate\n- Implement", result.Task.CurrentPlan);

        var persisted = await _runtime.GetTaskAsync(result.Task.Id);
        Assert.NotNull(persisted);
        Assert.Equal(result.Task.CurrentPlan, persisted!.CurrentPlan);
    }

    [Fact]
    public async Task CreateTask_ThrowsForMissingTitle()
    {
        var request = new TaskAssignmentRequest(
            Title: "",
            Description: "Some description",
            SuccessCriteria: "",
            RoomId: null,
            PreferredRoles: []
        );

        await Assert.ThrowsAsync<ArgumentException>(
            () => _runtime.CreateTaskAsync(request));
    }

    [Fact]
    public async Task GetTasks_ReturnsAll()
    {
        await _runtime.InitializeAsync();

        await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Task 1", "Desc 1", "Criteria", "main", []));
        await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Task 2", "Desc 2", "Criteria", null, []));

        var tasks = await _runtime.GetTasksAsync();
        Assert.Equal(2, tasks.Count);
    }

    [Fact]
    public async Task GetTasks_InActiveWorkspace_IncludesLegacyAndOrphanedHistory()
    {
        const string activeWorkspace = "/home/test/agent-academy";
        const string inactiveWorkspace = "/home/test/other-project";

        _db.Workspaces.AddRange(
            new WorkspaceEntity
            {
                Path = activeWorkspace,
                ProjectName = "Agent Academy",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            },
            new WorkspaceEntity
            {
                Path = inactiveWorkspace,
                ProjectName = "Other Project",
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            });

        var now = DateTime.UtcNow;
        _db.Rooms.AddRange(
            new RoomEntity
            {
                Id = "agent-academy-main",
                Name = "Main Collaboration Room",
                Status = "Idle",
                CurrentPhase = "Intake",
                WorkspacePath = activeWorkspace,
                CreatedAt = now,
                UpdatedAt = now
            },
            new RoomEntity
            {
                Id = "main",
                Name = "Main Collaboration Room",
                Status = "Idle",
                CurrentPhase = "Intake",
                WorkspacePath = null,
                CreatedAt = now,
                UpdatedAt = now
            },
            new RoomEntity
            {
                Id = "other-project-main",
                Name = "Other Project Main Room",
                Status = "Idle",
                CurrentPhase = "Intake",
                WorkspacePath = inactiveWorkspace,
                CreatedAt = now,
                UpdatedAt = now
            });

        _db.Tasks.AddRange(
            // Task with direct WorkspacePath (post-backfill or newly created)
            new TaskEntity
            {
                Id = "current-workspace-task",
                Title = "Current workspace task",
                Description = "Visible via direct WorkspacePath",
                SuccessCriteria = "Show up",
                RoomId = "agent-academy-main",
                WorkspacePath = activeWorkspace,
                CreatedAt = now.AddMinutes(-3),
                UpdatedAt = now.AddMinutes(-3)
            },
            // Legacy task: null WorkspacePath but room belongs to active workspace (fallback)
            new TaskEntity
            {
                Id = "unbackfilled-workspace-task",
                Title = "Unbackfilled workspace task",
                Description = "Visible via room fallback — room has matching workspace",
                SuccessCriteria = "Show up",
                RoomId = "agent-academy-main",
                WorkspacePath = null,
                CreatedAt = now.AddMinutes(-2),
                UpdatedAt = now.AddMinutes(-2)
            },
            // Legacy task in unscoped room — excluded from workspace view
            new TaskEntity
            {
                Id = "legacy-main-task",
                Title = "Legacy main task",
                Description = "Not in any workspace",
                SuccessCriteria = "Stay hidden",
                RoomId = "main",
                CreatedAt = now.AddMinutes(-1),
                UpdatedAt = now.AddMinutes(-1)
            },
            new TaskEntity
            {
                Id = "other-workspace-task",
                Title = "Other workspace task",
                Description = "Must stay hidden",
                SuccessCriteria = "Stay hidden",
                RoomId = "other-project-main",
                WorkspacePath = inactiveWorkspace,
                CreatedAt = now,
                UpdatedAt = now
            });
        await _db.SaveChangesAsync();

        var tasks = await _runtime.GetTasksAsync();

        Assert.Collection(tasks,
            task => Assert.Equal("unbackfilled-workspace-task", task.Id),
            task => Assert.Equal("current-workspace-task", task.Id));
    }

    [Fact]
    public async Task GetTask_ReturnsSingleTask()
    {
        await _runtime.InitializeAsync();

        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Task 1", "Desc 1", "Criteria", "main", []));

        var task = await _runtime.GetTaskAsync(result.Task.Id);
        Assert.NotNull(task);
        Assert.Equal("Task 1", task.Title);
    }

    [Fact]
    public async Task TransitionBreakoutTaskToInReview_UpdatesLinkedTask()
    {
        await _runtime.InitializeAsync();

        var taskResult = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Reviewable Task", "Needs review", "Reach InReview", "main", []));
        var breakout = await _runtime.CreateBreakoutRoomAsync("main", "engineer-1", "BR: Reviewable Task");
        await _runtime.SetBreakoutTaskIdAsync(breakout.Id, taskResult.Task.Id);

        var updatedTask = await _runtime.TransitionBreakoutTaskToInReviewAsync(breakout.Id);

        Assert.NotNull(updatedTask);
        Assert.Equal(Shared.Models.TaskStatus.InReview, updatedTask!.Status);

        var persistedTask = await _runtime.GetTaskAsync(taskResult.Task.Id);
        Assert.NotNull(persistedTask);
        Assert.Equal(Shared.Models.TaskStatus.InReview, persistedTask!.Status);
    }

    [Fact]
    public async Task SetPlanAsync_AllowsBreakoutRoomPlans()
    {
        await _runtime.InitializeAsync();

        var breakout = await _runtime.CreateBreakoutRoomAsync("main", "engineer-1", "BR: Plan Test");
        await _runtime.SetPlanAsync(breakout.Id, "# Breakout Plan\n\n- Do work");

        var plan = await _runtime.GetPlanAsync(breakout.Id);

        Assert.NotNull(plan);
        Assert.Contains("Breakout Plan", plan!.Content);
    }

    [Fact]
    public async Task CompleteTask_PersistsMergeCommitSha()
    {
        await _runtime.InitializeAsync();

        var taskResult = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Mergeable Task", "Needs merge metadata", "Persist merge SHA", "main", []));

        var completedTask = await _runtime.CompleteTaskAsync(
            taskResult.Task.Id,
            commitCount: 1,
            mergeCommitSha: "abc123def456");

        Assert.Equal(Shared.Models.TaskStatus.Completed, completedTask.Status);
        Assert.Equal("abc123def456", completedTask.MergeCommitSha);

        var persistedTask = await _runtime.GetTaskAsync(taskResult.Task.Id);
        Assert.NotNull(persistedTask);
        Assert.Equal("abc123def456", persistedTask!.MergeCommitSha);
    }

    // ── Phase Management ────────────────────────────────────────

    [Fact]
    public async Task TransitionPhase_UpdatesRoomAndTask()
    {
        await _runtime.InitializeAsync();

        // Create a task first (so there's an active task)
        await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Test task", "Testing phases", "Pass", "main", []));

        var room = await _runtime.TransitionPhaseAsync(
            "main", CollaborationPhase.Discussion, "Ready to discuss");

        Assert.Equal(CollaborationPhase.Discussion, room.CurrentPhase);
        Assert.Equal(RoomStatus.Active, room.Status);

        // Check the task was updated too
        var tasks = await _runtime.GetTasksAsync();
        var activeTask = tasks.FirstOrDefault(t => t.Status == Shared.Models.TaskStatus.Active);
        Assert.NotNull(activeTask);
        Assert.Equal(CollaborationPhase.Discussion, activeTask.CurrentPhase);
    }

    [Fact]
    public async Task TransitionPhase_FinalSynthesisCompletesRoom()
    {
        await _runtime.InitializeAsync();

        var room = await _runtime.TransitionPhaseAsync(
            "main", CollaborationPhase.FinalSynthesis);

        Assert.Equal(RoomStatus.Completed, room.Status);
        Assert.Equal(CollaborationPhase.FinalSynthesis, room.CurrentPhase);
    }

    [Fact]
    public async Task TransitionPhase_NoOpForSamePhase()
    {
        await _runtime.InitializeAsync();

        // Default phase is Intake
        var room = await _runtime.TransitionPhaseAsync(
            "main", CollaborationPhase.Intake);

        Assert.Equal(CollaborationPhase.Intake, room.CurrentPhase);
    }

    [Fact]
    public async Task TransitionPhase_ThrowsForMissingRoom()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _runtime.TransitionPhaseAsync("nonexistent", CollaborationPhase.Planning));
    }

    [Fact]
    public async Task TransitionPhase_AddsCoordinationMessage()
    {
        await _runtime.InitializeAsync();

        await _runtime.TransitionPhaseAsync(
            "main", CollaborationPhase.Planning, "Let's plan");

        var room = await _runtime.GetRoomAsync("main");
        Assert.NotNull(room);
        Assert.Contains(room.RecentMessages,
            m => m.Content.Contains("Phase changed from Intake to Planning")
                 && m.Content.Contains("Let's plan"));
    }

    // ── Agent Location ──────────────────────────────────────────

    [Fact]
    public async Task MoveAgent_UpdatesLocation()
    {
        await _runtime.InitializeAsync();

        var loc = await _runtime.MoveAgentAsync(
            "planner-1", "main", AgentState.Working);

        Assert.Equal("main", loc.RoomId);
        Assert.Equal(AgentState.Working, loc.State);
        Assert.Null(loc.BreakoutRoomId);
    }

    [Fact]
    public async Task MoveAgent_ThrowsForUnknownAgent()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _runtime.MoveAgentAsync("unknown", "main", AgentState.Idle));
    }

    [Fact]
    public async Task GetAgentLocation_ReturnsSingle()
    {
        await _runtime.InitializeAsync();

        var loc = await _runtime.GetAgentLocationAsync("planner-1");
        Assert.NotNull(loc);
        Assert.Equal("main", loc.RoomId);
    }

    // ── Breakout Rooms ──────────────────────────────────────────

    [Fact]
    public async Task CreateBreakoutRoom_CreatesAndMovesAgent()
    {
        await _runtime.InitializeAsync();

        var breakout = await _runtime.CreateBreakoutRoomAsync(
            "main", "engineer-1", "Auth Implementation");

        Assert.Equal("main", breakout.ParentRoomId);
        Assert.Equal("engineer-1", breakout.AssignedAgentId);
        Assert.Equal("Auth Implementation", breakout.Name);
        Assert.Equal(RoomStatus.Active, breakout.Status);

        // Agent should be in "Working" state
        var loc = await _runtime.GetAgentLocationAsync("engineer-1");
        Assert.NotNull(loc);
        Assert.Equal(AgentState.Working, loc.State);
        Assert.Equal(breakout.Id, loc.BreakoutRoomId);

        // Agent in breakout should NOT appear in parent room participants
        var room = await _runtime.GetRoomAsync("main");
        Assert.NotNull(room);
        Assert.DoesNotContain(room.Participants, p => p.AgentId == "engineer-1");
    }

    [Fact]
    public async Task CloseBreakoutRoom_MovesAgentBackToIdle()
    {
        await _runtime.InitializeAsync();

        var breakout = await _runtime.CreateBreakoutRoomAsync(
            "main", "engineer-1", "Auth Implementation");

        await _runtime.CloseBreakoutRoomAsync(breakout.Id);

        // Agent should be back to idle
        var loc = await _runtime.GetAgentLocationAsync("engineer-1");
        Assert.NotNull(loc);
        Assert.Equal(AgentState.Idle, loc.State);
        Assert.Null(loc.BreakoutRoomId);

        // Breakout room should be archived (not visible in active list)
        var breakouts = await _runtime.GetBreakoutRoomsAsync("main");
        Assert.Empty(breakouts);

        // But still retrievable by ID (soft-deleted, not hard-deleted)
        var archived = await _runtime.GetBreakoutRoomAsync(breakout.Id);
        Assert.NotNull(archived);
        Assert.Equal(RoomStatus.Archived, archived.Status);

        var entity = await _db.BreakoutRooms.FindAsync(breakout.Id);
        Assert.NotNull(entity);
        Assert.Equal(nameof(BreakoutRoomCloseReason.Completed), entity.CloseReason);
    }

    [Fact]
    public async Task GetBreakoutRooms_ReturnsForParent()
    {
        await _runtime.InitializeAsync();

        await _runtime.CreateBreakoutRoomAsync("main", "engineer-1", "Breakout 1");
        await _runtime.CreateBreakoutRoomAsync("main", "planner-1", "Breakout 2");

        var breakouts = await _runtime.GetBreakoutRoomsAsync("main");
        Assert.Equal(2, breakouts.Count);
    }

    [Fact]
    public async Task CreateBreakoutRoom_ThrowsForMissingRoom()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _runtime.CreateBreakoutRoomAsync("nonexistent", "engineer-1", "Test"));
    }

    [Fact]
    public async Task CreateBreakoutRoom_ThrowsForUnknownAgent()
    {
        await _runtime.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _runtime.CreateBreakoutRoomAsync("main", "unknown-agent", "Test"));
    }

    // ── Plan Management ─────────────────────────────────────────

    [Fact]
    public async Task PlanLifecycle_SetGetDelete()
    {
        await _runtime.InitializeAsync();

        // Initially no plan
        var plan = await _runtime.GetPlanAsync("main");
        Assert.Null(plan);

        // Set plan
        await _runtime.SetPlanAsync("main", "Step 1: Design\nStep 2: Build");
        plan = await _runtime.GetPlanAsync("main");
        Assert.NotNull(plan);
        Assert.Contains("Step 1", plan.Content);

        // Update plan
        await _runtime.SetPlanAsync("main", "Updated plan");
        plan = await _runtime.GetPlanAsync("main");
        Assert.Equal("Updated plan", plan!.Content);

        // Delete plan
        var deleted = await _runtime.DeletePlanAsync("main");
        Assert.True(deleted);

        plan = await _runtime.GetPlanAsync("main");
        Assert.Null(plan);

        // Delete again returns false
        deleted = await _runtime.DeletePlanAsync("main");
        Assert.False(deleted);
    }

    // ── Activity Publishing ─────────────────────────────────────

    [Fact]
    public async Task PublishThinking_EmitsEvent()
    {
        await _runtime.InitializeAsync();

        ActivityEvent? received = null;
        _runtime.StreamActivity(evt => received = evt);

        var agent = _catalog.Agents[0];
        await _runtime.PublishThinkingAsync(agent, "main");

        Assert.NotNull(received);
        Assert.Equal(ActivityEventType.AgentThinking, received.Type);
        Assert.Contains("thinking", received.Message);
    }

    [Fact]
    public async Task PublishFinished_EmitsEvent()
    {
        await _runtime.InitializeAsync();

        ActivityEvent? received = null;
        _runtime.StreamActivity(evt => received = evt);

        var agent = _catalog.Agents[0];
        await _runtime.PublishFinishedAsync(agent, "main");

        Assert.NotNull(received);
        Assert.Equal(ActivityEventType.AgentFinished, received.Type);
        Assert.Contains("finished", received.Message);
    }

    [Fact]
    public async Task GetRecentActivity_ReturnsBufferedEvents()
    {
        await _runtime.InitializeAsync();

        // Initialize generates several events
        var activity = _runtime.GetRecentActivity();
        Assert.NotEmpty(activity);
        Assert.Contains(activity, e => e.Type == ActivityEventType.RoomCreated);
    }

    [Fact]
    public async Task StreamActivity_Unsubscribe()
    {
        await _runtime.InitializeAsync();

        var count = 0;
        var unsub = _runtime.StreamActivity(_ => count++);

        await _runtime.PublishThinkingAsync(_catalog.Agents[0], "main");
        Assert.Equal(1, count);

        unsub();
        await _runtime.PublishFinishedAsync(_catalog.Agents[0], "main");
        Assert.Equal(1, count); // Should not increment
    }

    // ── Workspace Overview ──────────────────────────────────────

    [Fact]
    public async Task GetOverview_ReturnsFullState()
    {
        await _runtime.InitializeAsync();

        var overview = await _runtime.GetOverviewAsync();

        Assert.Equal(3, overview.ConfiguredAgents.Count);
        Assert.Single(overview.Rooms);
        Assert.Equal(3, overview.AgentLocations.Count);
        Assert.NotEmpty(overview.RecentActivity);
    }

    // ── Configured Agents ───────────────────────────────────────

    [Fact]
    public void GetConfiguredAgents_ReturnsCatalogAgents()
    {
        var agents = _runtime.GetConfiguredAgents();
        Assert.Equal(3, agents.Count);
        Assert.Equal("Aristotle", agents[0].Name);
    }

    // ── GetProjectNameForRoomAsync ──────────────────────────────

    [Fact]
    public async Task GetProjectNameForRoom_ReturnsProjectName_WhenWorkspaceHasProjectName()
    {
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/home/test/my-project",
            ProjectName = "My Project",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "room-1",
            Name = "Test Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            WorkspacePath = "/home/test/my-project",
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var result = await _runtime.GetProjectNameForRoomAsync("room-1");
        Assert.Equal("My Project", result);
    }

    [Fact]
    public async Task GetProjectNameForRoom_ReturnsNull_WhenRoomHasNoWorkspace()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "legacy-room",
            Name = "Legacy Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            WorkspacePath = null,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var result = await _runtime.GetProjectNameForRoomAsync("legacy-room");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetProjectNameForRoom_ReturnsNull_WhenRoomDoesNotExist()
    {
        var result = await _runtime.GetProjectNameForRoomAsync("nonexistent-room");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetProjectNameForRoom_FallsBackToDirectoryBasename_WhenProjectNameIsNull()
    {
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/home/test/cool-app",
            ProjectName = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "room-no-name",
            Name = "Room Without Project Name",
            Status = "Idle",
            CurrentPhase = "Intake",
            WorkspacePath = "/home/test/cool-app",
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var result = await _runtime.GetProjectNameForRoomAsync("room-no-name");
        Assert.Equal("Cool App", result);
    }

    [Fact]
    public async Task GetProjectNameForRoom_ReturnsNull_WhenWorkspaceEntityMissing()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "orphan-room",
            Name = "Orphan Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            WorkspacePath = "/home/test/deleted-project",
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var result = await _runtime.GetProjectNameForRoomAsync("orphan-room");
        Assert.Null(result);
    }

    // ── Duplicate main room retirement ─────────────────────────

    [Fact]
    public async Task EnsureDefaultRoom_RetiresLegacyRoom_WhenBackfilledIntoWorkspace()
    {
        var workspacePath = "/home/test/my-project";
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = workspacePath,
            ProjectName = "My Project",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        // Simulate the migration backfill: legacy "main" room gets WorkspacePath set
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "main",
            Name = "Main Collaboration Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            WorkspacePath = workspacePath,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        // This should create a workspace default room AND retire the legacy one
        var defaultRoomId = await _runtime.EnsureDefaultRoomForWorkspaceAsync(workspacePath);

        Assert.NotEqual("main", defaultRoomId);

        // Legacy room should have WorkspacePath cleared
        var legacyRoom = await _db.Rooms.FindAsync("main");
        Assert.NotNull(legacyRoom);
        Assert.Null(legacyRoom!.WorkspacePath);

        // Only the workspace default room should appear in room list
        var rooms = await _runtime.GetRoomsAsync();
        Assert.Single(rooms);
        Assert.Equal(defaultRoomId, rooms[0].Id);
    }

    [Fact]
    public async Task EnsureDefaultRoom_RetiresLegacyRoom_WhenWorkspaceAlreadyHasDefaultRoom()
    {
        var workspacePath = "/home/test/my-project";
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = workspacePath,
            ProjectName = "My Project",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        var now = DateTime.UtcNow;

        // Legacy room backfilled into workspace
        _db.Rooms.Add(new RoomEntity
        {
            Id = "main",
            Name = "Main Collaboration Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            WorkspacePath = workspacePath,
            CreatedAt = now,
            UpdatedAt = now
        });

        // Workspace already has its own default room
        _db.Rooms.Add(new RoomEntity
        {
            Id = "my-project-main",
            Name = "Main Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            WorkspacePath = workspacePath,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        // Should find existing workspace room and retire legacy
        var defaultRoomId = await _runtime.EnsureDefaultRoomForWorkspaceAsync(workspacePath);
        Assert.Equal("my-project-main", defaultRoomId);

        var legacyRoom = await _db.Rooms.FindAsync("main");
        Assert.Null(legacyRoom!.WorkspacePath);

        var rooms = await _runtime.GetRoomsAsync();
        Assert.Single(rooms);
        Assert.Equal("my-project-main", rooms[0].Id);
    }

    [Fact]
    public async Task EnsureDefaultRoom_DoesNotRetire_WhenLegacyRoomNotBackfilled()
    {
        var workspacePath = "/home/test/my-project";
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = workspacePath,
            ProjectName = "My Project",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        // Legacy room with no workspace (never backfilled)
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "main",
            Name = "Main Collaboration Room",
            Status = "Idle",
            CurrentPhase = "Intake",
            WorkspacePath = null,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        await _runtime.EnsureDefaultRoomForWorkspaceAsync(workspacePath);

        // Legacy room should still have null WorkspacePath (unchanged)
        var legacyRoom = await _db.Rooms.FindAsync("main");
        Assert.Null(legacyRoom!.WorkspacePath);
    }

    // ── RenameRoomAsync ────────────────────────────────────────

    [Fact]
    public async Task RenameRoom_UpdatesNameAndPublishesEvent()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "room-to-rename",
            Name = "Old Name",
            Status = "Idle",
            CurrentPhase = "Intake",
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        ActivityEvent? capturedEvent = null;
        _activityBus.Subscribe(evt =>
        {
            if (evt.Type == ActivityEventType.RoomRenamed)
                capturedEvent = evt;
        });

        var result = await _runtime.RenameRoomAsync("room-to-rename", "New Name");

        Assert.NotNull(result);
        Assert.Equal("New Name", result!.Name);

        // Verify DB was updated
        var room = await _db.Rooms.FindAsync("room-to-rename");
        Assert.Equal("New Name", room!.Name);

        // Verify activity event was published
        Assert.NotNull(capturedEvent);
        Assert.Equal("room-to-rename", capturedEvent!.RoomId);
        Assert.Contains("New Name", capturedEvent.Message);
    }

    [Fact]
    public async Task RenameRoom_ReturnsNull_WhenRoomDoesNotExist()
    {
        var result = await _runtime.RenameRoomAsync("nonexistent", "New Name");
        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureDefaultRoom_UsesConfiguredDefaultRoomName()
    {
        var workspacePath = "/home/test/my-project";
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = workspacePath,
            ProjectName = "My Project",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var defaultRoomId = await _runtime.EnsureDefaultRoomForWorkspaceAsync(workspacePath);
        var room = await _db.Rooms.FindAsync(defaultRoomId);

        Assert.Equal(_catalog.DefaultRoomName, room!.Name);
    }

    // ── Stale Room Cleanup ──────────────────────────────────────

    [Fact]
    public async Task CompleteTask_AutoArchivesRoomWhenAllTasksTerminal()
    {
        await _runtime.InitializeAsync();

        // Create a task in a new room (not main)
        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Auto-archive test", "Test room cleanup", "Room gets archived", null, []));

        var roomId = result.Room.Id;
        Assert.NotEqual("main", roomId);

        // Complete the task
        await _runtime.CompleteTaskAsync(result.Task.Id, commitCount: 1);

        // Room should now be archived
        var room = await _runtime.GetRoomAsync(roomId);
        Assert.NotNull(room);
        Assert.Equal(RoomStatus.Archived, room!.Status);
    }

    [Fact]
    public async Task CompleteTask_DoesNotArchiveRoomWithActiveTask()
    {
        await _runtime.InitializeAsync();

        // Create two tasks in the same room
        var result1 = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "First task", "Desc", "Criteria", null, []));
        var roomId = result1.Room.Id;

        var result2 = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Second task", "Desc", "Criteria", roomId, []));

        // Complete only the first task
        await _runtime.CompleteTaskAsync(result1.Task.Id, commitCount: 1);

        // Room should still be active (second task remains)
        var room = await _runtime.GetRoomAsync(roomId);
        Assert.NotNull(room);
        Assert.NotEqual(RoomStatus.Archived, room!.Status);
    }

    [Fact]
    public async Task CompleteTask_ArchivesRoomWhenAllTasksCancelledOrCompleted()
    {
        await _runtime.InitializeAsync();

        var result1 = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Task one", "Desc", "Criteria", null, []));
        var roomId = result1.Room.Id;

        var result2 = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Task two", "Desc", "Criteria", roomId, []));

        // Cancel task 2 via direct DB update (simulating CancelTaskHandler)
        var task2Entity = await _db.Tasks.FindAsync(result2.Task.Id);
        task2Entity!.Status = nameof(Shared.Models.TaskStatus.Cancelled);
        await _db.SaveChangesAsync();

        // Complete task 1 — now both tasks are terminal
        await _runtime.CompleteTaskAsync(result1.Task.Id, commitCount: 1);

        var room = await _runtime.GetRoomAsync(roomId);
        Assert.NotNull(room);
        Assert.Equal(RoomStatus.Archived, room!.Status);
    }

    [Fact]
    public async Task CompleteTask_DoesNotArchiveMainRoom()
    {
        await _runtime.InitializeAsync();

        // Create task in main room
        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Main room task", "Desc", "Criteria", "main", []));

        // Complete the task
        await _runtime.CompleteTaskAsync(result.Task.Id, commitCount: 1);

        // Main room should NOT be archived
        var room = await _runtime.GetRoomAsync("main");
        Assert.NotNull(room);
        Assert.NotEqual(RoomStatus.Archived, room!.Status);
    }

    [Fact]
    public async Task CompleteTask_EvacuatesAgentsOnArchive()
    {
        await _runtime.InitializeAsync();

        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Evacuation test", "Desc", "Criteria", null, []));
        var roomId = result.Room.Id;

        // Move an agent into the task room
        await _runtime.MoveAgentAsync("engineer-1", roomId, AgentState.InRoom);

        // Complete the task
        await _runtime.CompleteTaskAsync(result.Task.Id, commitCount: 1);

        // Agent should have been moved back to the default room
        var location = await _runtime.GetAgentLocationAsync("engineer-1");
        Assert.NotNull(location);
        Assert.Equal("main", location!.RoomId);
        Assert.Equal(AgentState.Idle, location.State);
    }

    [Fact]
    public async Task GetRoomsAsync_ExcludesArchivedByDefault()
    {
        await _runtime.InitializeAsync();

        // Create and auto-archive a room
        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Archived room test", "Desc", "Criteria", null, []));
        var roomId = result.Room.Id;
        await _runtime.CompleteTaskAsync(result.Task.Id, commitCount: 1);

        // Default query should not include archived room
        var rooms = await _runtime.GetRoomsAsync();
        Assert.DoesNotContain(rooms, r => r.Id == roomId);

        // With includeArchived should include it
        var allRooms = await _runtime.GetRoomsAsync(includeArchived: true);
        Assert.Contains(allRooms, r => r.Id == roomId);
    }

    [Fact]
    public async Task RejectTask_ReopensAutoArchivedRoom()
    {
        await _runtime.InitializeAsync();

        // Create task, approve, complete (auto-archive)
        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Reject test", "Desc", "Criteria", null, []));
        var roomId = result.Room.Id;

        var taskEntity = await _db.Tasks.FindAsync(result.Task.Id);
        taskEntity!.Status = nameof(Shared.Models.TaskStatus.Approved);
        await _db.SaveChangesAsync();

        await _runtime.CompleteTaskAsync(result.Task.Id, commitCount: 1);

        // Verify room is archived
        var archivedRoom = await _runtime.GetRoomAsync(roomId);
        Assert.Equal(RoomStatus.Archived, archivedRoom!.Status);

        // Reject the task — should reopen the room
        await _runtime.RejectTaskAsync(result.Task.Id, "reviewer-1", "Needs more tests");

        var reopenedRoom = await _runtime.GetRoomAsync(roomId);
        Assert.NotNull(reopenedRoom);
        Assert.Equal(RoomStatus.Active, reopenedRoom!.Status);
    }

    [Fact]
    public async Task CleanupStaleRooms_ArchivesStaleRooms()
    {
        await _runtime.InitializeAsync();

        // Create a task and manually complete it (without triggering auto-archive by
        // directly updating the DB, simulating a pre-existing stale room)
        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Stale room", "Desc", "Criteria", null, []));
        var roomId = result.Room.Id;

        // Mark task completed directly in DB (bypassing auto-archive)
        var entity = await _db.Tasks.FindAsync(result.Task.Id);
        entity!.Status = nameof(Shared.Models.TaskStatus.Completed);
        entity.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Room should still be Active
        var room = await _db.Rooms.FindAsync(roomId);
        Assert.Equal(nameof(RoomStatus.Active), room!.Status);

        // Run cleanup
        var count = await _runtime.CleanupStaleRoomsAsync();

        Assert.Equal(1, count);
        var cleaned = await _db.Rooms.FindAsync(roomId);
        Assert.Equal(nameof(RoomStatus.Archived), cleaned!.Status);
    }

    [Fact]
    public async Task CleanupStaleRooms_SkipsRoomsWithNoTasks()
    {
        await _runtime.InitializeAsync();

        // Create an empty room (no tasks)
        var room = await _runtime.CreateRoomAsync("Empty Room");

        var count = await _runtime.CleanupStaleRoomsAsync();

        Assert.Equal(0, count);
        var entity = await _db.Rooms.FindAsync(room.Id);
        Assert.NotEqual(nameof(RoomStatus.Archived), entity!.Status);
    }

    [Fact]
    public async Task CleanupStaleRooms_SkipsMainRoom()
    {
        await _runtime.InitializeAsync();

        // Create task in main room and complete it
        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Main task", "Desc", "Criteria", "main", []));

        var entity = await _db.Tasks.FindAsync(result.Task.Id);
        entity!.Status = nameof(Shared.Models.TaskStatus.Completed);
        entity.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var count = await _runtime.CleanupStaleRoomsAsync();

        Assert.Equal(0, count);
        var mainRoom = await _db.Rooms.FindAsync("main");
        Assert.NotEqual(nameof(RoomStatus.Archived), mainRoom!.Status);
    }

    [Fact]
    public async Task CleanupStaleRooms_SkipsRoomsWithActiveTasks()
    {
        await _runtime.InitializeAsync();

        var result1 = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Done task", "Desc", "Criteria", null, []));
        var roomId = result1.Room.Id;

        var result2 = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Active task", "Desc", "Criteria", roomId, []));

        // Complete only first task
        var entity = await _db.Tasks.FindAsync(result1.Task.Id);
        entity!.Status = nameof(Shared.Models.TaskStatus.Completed);
        entity.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var count = await _runtime.CleanupStaleRoomsAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CompleteTask_PublishesAutoArchiveActivityEvent()
    {
        await _runtime.InitializeAsync();

        var events = new List<ActivityEvent>();
        _activityBus.Subscribe(e => events.Add(e));

        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Activity test", "Desc", "Criteria", null, []));

        events.Clear();
        await _runtime.CompleteTaskAsync(result.Task.Id, commitCount: 1);

        var archiveEvent = events.FirstOrDefault(e =>
            e.Type == ActivityEventType.RoomClosed &&
            e.Message.Contains("auto-archived"));
        Assert.NotNull(archiveEvent);
    }
}
