using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for WorkspaceRuntime — the central state manager.
/// Uses in-memory SQLite for isolation.
/// </summary>
public class WorkspaceRuntimeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly WorkspaceRuntime _runtime;
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityBroadcaster _activityBus;

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
        _runtime = new WorkspaceRuntime(_db, logger, _catalog, _activityBus);
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
        Assert.Equal("You", envelope.SenderName);
        Assert.Equal(MessageSenderKind.User, envelope.SenderKind);
        Assert.Equal(MessageKind.Response, envelope.Kind);
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
    public async Task GetTask_ReturnsSingleTask()
    {
        await _runtime.InitializeAsync();

        var result = await _runtime.CreateTaskAsync(new TaskAssignmentRequest(
            "Task 1", "Desc 1", "Criteria", "main", []));

        var task = await _runtime.GetTaskAsync(result.Task.Id);
        Assert.NotNull(task);
        Assert.Equal("Task 1", task.Title);
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

        // Breakout room should be removed
        var breakouts = await _runtime.GetBreakoutRoomsAsync("main");
        Assert.Empty(breakouts);
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
}
