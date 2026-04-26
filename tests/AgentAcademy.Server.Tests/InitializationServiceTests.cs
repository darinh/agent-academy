using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class InitializationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityBroadcaster _activityBus;
    private readonly ActivityPublisher _activity;
    private readonly CrashRecoveryService _crashRecovery;
    private readonly RoomService _roomService;
    private readonly WorkspaceRoomService _workspaceRoomService;

    public InitializationServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        var agents = new List<AgentDefinition>
        {
            new("agent-1", "TestBot", "Engineer", "Test summary", "prompt", null,
                ["coding"], ["task-state"], true),
            new("agent-2", "ReviewBot", "Reviewer", "Test summary", "prompt", null,
                ["review"], ["task-state"], true),
        };
        _catalog = new AgentCatalogOptions("main-room", "Main Room", agents);

        _activityBus = new ActivityBroadcaster();
        _activity = new ActivityPublisher(_db, _activityBus);

        var executor = Substitute.For<IAgentExecutor>();
        var settings = new SystemSettingsService(_db);
        var session = new ConversationSessionService(
            _db, settings, executor, new TestDoubles.NoOpWatchdogAgentRunner(executor),
            NullLogger<ConversationSessionService>.Instance);
        var messageService = new MessageService(
            _db, NullLogger<MessageService>.Instance, _catalog,
            _activity, session, new MessageBroadcaster());
        var agentLocations = new AgentLocationService(_db, _catalog, _activity);
        var taskDeps = new TaskDependencyService(_db, NullLogger<TaskDependencyService>.Instance, _activity);
        var taskQueries = new TaskQueryService(
            _db, NullLogger<TaskQueryService>.Instance, _catalog, taskDeps);
        var breakouts = new BreakoutRoomService(
            _db, NullLogger<BreakoutRoomService>.Instance, _catalog,
            _activity, session, taskQueries, agentLocations);

        _crashRecovery = new CrashRecoveryService(
            _db, NullLogger<CrashRecoveryService>.Instance,
            breakouts, agentLocations, messageService, _activity);

        var snapshots = new RoomSnapshotBuilder(_db, _catalog, new PhaseTransitionValidator(_db));
        _roomService = new RoomService(
            _db, NullLogger<RoomService>.Instance,
            _activity, messageService, snapshots, new PhaseTransitionValidator(_db), _catalog);
        _workspaceRoomService = new WorkspaceRoomService(
            _db, NullLogger<WorkspaceRoomService>.Instance, _catalog, _activity);
    }

    private InitializationService CreateSut(
        AgentCatalogOptions? catalogOverride = null,
        IWorktreeService? worktrees = null)
    {
        var cat = catalogOverride ?? _catalog;
        return new InitializationService(
            _db,
            NullLogger<InitializationService>.Instance,
            cat,
            _activity,
            _crashRecovery,
            _roomService,
            _workspaceRoomService,
            worktrees);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── 1. Default room creation ──────────────────────────────────────

    [Fact]
    public async Task Initialize_NoWorkspace_CreatesDefaultRoom()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        var room = await _db.Rooms.FindAsync("main-room");
        Assert.NotNull(room);
        Assert.Equal("Main Room", room.Name);
        Assert.Equal(nameof(RoomStatus.Idle), room.Status);
        Assert.Equal(nameof(CollaborationPhase.Intake), room.CurrentPhase);
    }

    // ── 2. Welcome message ────────────────────────────────────────────

    [Fact]
    public async Task Initialize_NoWorkspace_CreatesWelcomeMessage()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        var messages = await _db.Messages
            .Where(m => m.RoomId == "main-room")
            .ToListAsync();

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal("system", msg.SenderId);
        Assert.Equal("System", msg.SenderName);
        Assert.Equal(nameof(MessageSenderKind.System), msg.SenderKind);
        Assert.Equal(nameof(MessageKind.System), msg.Kind);
        Assert.Contains("Collaboration host started", msg.Content);
    }

    // ── 3. RoomCreated activity event ─────────────────────────────────

    [Fact]
    public async Task Initialize_NoWorkspace_PublishesRoomCreatedEvent()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        var events = await _db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.RoomCreated))
            .ToListAsync();

        Assert.Single(events);
        Assert.Equal("main-room", events[0].RoomId);
        Assert.Contains("Main Room", events[0].Message);
    }

    // ── 4. AgentLoaded activity events ────────────────────────────────

    [Fact]
    public async Task Initialize_NoWorkspace_PublishesAgentLoadedForEachAgent()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        var events = await _db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.AgentLoaded))
            .ToListAsync();

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Message.Contains("TestBot"));
        Assert.Contains(events, e => e.Message.Contains("ReviewBot"));
    }

    // ── 5. Existing room — no duplicate ───────────────────────────────

    [Fact]
    public async Task Initialize_ExistingDefaultRoom_DoesNotCreateDuplicate()
    {
        _db.Rooms.Add(new RoomEntity
        {
            Id = "main-room",
            Name = "Existing Room",
            Status = nameof(RoomStatus.Active),
            CurrentPhase = nameof(CollaborationPhase.Discussion),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var sut = CreateSut();
        await sut.InitializeAsync();

        var rooms = await _db.Rooms
            .Where(r => r.Id == "main-room")
            .ToListAsync();

        Assert.Single(rooms);
        Assert.Equal("Existing Room", rooms[0].Name);
        Assert.Equal(nameof(RoomStatus.Active), rooms[0].Status);
    }

    // ── 6. Active workspace — skip default room ───────────────────────

    [Fact]
    public async Task Initialize_WithActiveWorkspace_SkipsDefaultRoomCreation()
    {
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/test/workspace",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var sut = CreateSut();
        await sut.InitializeAsync();

        var room = await _db.Rooms.FindAsync("main-room");
        Assert.Null(room);

        var welcomeMessages = await _db.Messages
            .Where(m => m.RoomId == "main-room")
            .CountAsync();
        Assert.Equal(0, welcomeMessages);
    }

    // ── 7. Agent location seeding ─────────────────────────────────────

    [Fact]
    public async Task Initialize_SeedsAgentLocations()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        var locations = await _db.AgentLocations.ToListAsync();
        Assert.Equal(2, locations.Count);
        Assert.Contains(locations, l => l.AgentId == "agent-1");
        Assert.Contains(locations, l => l.AgentId == "agent-2");
    }

    // ── 8. Existing agent locations preserved ─────────────────────────

    [Fact]
    public async Task Initialize_ExistingAgentLocations_DoesNotDuplicate()
    {
        _db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = "agent-1",
            RoomId = "other-room",
            State = nameof(AgentState.Working),
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var sut = CreateSut();
        await sut.InitializeAsync();

        var locations = await _db.AgentLocations.ToListAsync();
        Assert.Equal(2, locations.Count);

        var agent1 = await _db.AgentLocations.FindAsync("agent-1");
        Assert.NotNull(agent1);
        Assert.Equal("other-room", agent1.RoomId);
        Assert.Equal(nameof(AgentState.Working), agent1.State);
    }

    // ── 9. RecordServerInstance called ─────────────────────────────────

    [Fact]
    public async Task Initialize_CallsRecordServerInstance()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        var instanceCount = await _db.ServerInstances.CountAsync();
        Assert.True(instanceCount >= 1, "Expected at least one server instance record");
    }

    // ── 10. ResolveStartupMainRoomId called ───────────────────────────

    [Fact]
    public async Task Initialize_CallsResolveStartupMainRoomId()
    {
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/active/workspace",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var sut = CreateSut();

        // InitializeAsync must complete without error, which means
        // ResolveStartupMainRoomIdAsync ran with the workspace path.
        var exception = await Record.ExceptionAsync(() => sut.InitializeAsync());
        Assert.Null(exception);

        // Agent locations are still seeded even when workspace is active.
        var locations = await _db.AgentLocations.CountAsync();
        Assert.Equal(2, locations);
    }

    // ── 11. Empty agent list — room still created ─────────────────────

    [Fact]
    public async Task Initialize_NoAgents_StillCreatesRoom()
    {
        var emptyCatalog = new AgentCatalogOptions("main-room", "Main Room", []);
        var sut = CreateSut(emptyCatalog);
        await sut.InitializeAsync();

        var room = await _db.Rooms.FindAsync("main-room");
        Assert.NotNull(room);
        Assert.Equal("Main Room", room.Name);

        var agentLoadedEvents = await _db.ActivityEvents
            .Where(e => e.Type == nameof(ActivityEventType.AgentLoaded))
            .CountAsync();
        Assert.Equal(0, agentLoadedEvents);

        var locationCount = await _db.AgentLocations.CountAsync();
        Assert.Equal(0, locationCount);
    }

    // ── 12. Agent locations use default room ID ───────────────────────

    [Fact]
    public async Task Initialize_AgentLocationsUseDefaultRoomId()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        var locations = await _db.AgentLocations.ToListAsync();
        Assert.All(locations, l => Assert.Equal("main-room", l.RoomId));
        Assert.All(locations, l => Assert.Equal(nameof(AgentState.Idle), l.State));
    }

    // ── 13. Worktree sync on startup ──────────────────────────────────
    // Regression for the audit gap: SyncWithGitAsync existed but was never
    // called in production, so on server restart the in-memory worktree
    // tracking dictionary was empty until something queried git directly.

    [Fact]
    public async Task Initialize_WithWorktreeService_CallsSyncWithGitAsync()
    {
        var worktrees = Substitute.For<IWorktreeService>();
        var sut = CreateSut(worktrees: worktrees);

        await sut.InitializeAsync();

        await worktrees.Received(1).SyncWithGitAsync();
    }

    [Fact]
    public async Task Initialize_WithoutWorktreeService_StillSucceeds()
    {
        // IWorktreeService is optional in the InitializationService ctor so
        // unit tests that don't care about worktrees keep working unchanged.
        var sut = CreateSut(worktrees: null);

        await sut.InitializeAsync(); // must not throw

        var room = await _db.Rooms.FindAsync("main-room");
        Assert.NotNull(room);
    }

    [Fact]
    public async Task Initialize_WorktreeSyncThrows_DoesNotFailInitialization()
    {
        // Sync failures during startup should be logged and swallowed —
        // the rest of init (rooms, agent locations, crash recovery) must
        // still complete so the server can come up.
        var worktrees = Substitute.For<IWorktreeService>();
        worktrees.SyncWithGitAsync().Returns(Task.FromException(new InvalidOperationException("git is unhappy")));
        var sut = CreateSut(worktrees: worktrees);

        await sut.InitializeAsync(); // must not throw

        var room = await _db.Rooms.FindAsync("main-room");
        Assert.NotNull(room);
        var locations = await _db.AgentLocations.CountAsync();
        Assert.Equal(_catalog.Agents.Count, locations);
    }
}
