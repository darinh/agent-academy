using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for room management endpoints: create room, create session,
/// add/remove agents; and custom agent CRUD endpoints.
/// </summary>
public sealed class RoomAgentEndpointTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly RoomService _roomService;
    private readonly AgentLocationService _agentLocationService;
    private readonly IMessageService _messageService;
    private readonly BreakoutRoomService _breakoutRoomService;
    private readonly ConversationSessionService _sessionService;
    private readonly IAgentConfigService _configService;
    private readonly AgentCatalogOptions _catalog;
    private readonly string _mainRoomId = "main";

    private static readonly AgentDefinition TestAgent = new(
        Id: "planner",
        Name: "Planner",
        Role: "Planner",
        Summary: "Plans work",
        StartupPrompt: "You plan.",
        Model: "gpt-5",
        CapabilityTags: ["planning"],
        EnabledTools: ["bash"],
        AutoJoinDefaultRoom: true);

    public RoomAgentEndpointTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: _mainRoomId,
            DefaultRoomName: "Main Collaboration Room",
            Agents: [TestAgent]);


        var activityBus = new ActivityBroadcaster();
        var activityPublisher = new ActivityPublisher(_db, activityBus);
        var settingsService = new SystemSettingsService(_db);
        var executor = Substitute.For<IAgentExecutor>();
        var sessionLogger = Substitute.For<ILogger<ConversationSessionService>>();
        _sessionService = new ConversationSessionService(_db, settingsService, executor, new TestDoubles.NoOpWatchdogAgentRunner(executor), sessionLogger);
        var taskDeps = new TaskDependencyService(_db, NullLogger<TaskDependencyService>.Instance, activityPublisher);
        var taskQueries = new TaskQueryService(_db, NullLogger<TaskQueryService>.Instance, _catalog, taskDeps);
        var taskLifecycle = new TaskLifecycleService(_db, NullLogger<TaskLifecycleService>.Instance, _catalog, activityPublisher, taskDeps);
        var agentLocations = new AgentLocationService(_db, _catalog, activityPublisher);
        var planService = new PlanService(_db);
        var messageService = new MessageService(_db, NullLogger<MessageService>.Instance, _catalog, activityPublisher, _sessionService, new MessageBroadcaster());
        var breakouts = new BreakoutRoomService(_db, NullLogger<BreakoutRoomService>.Instance, _catalog, activityPublisher, _sessionService, taskQueries, agentLocations);
        var crashRecovery = new CrashRecoveryService(_db, NullLogger<CrashRecoveryService>.Instance, breakouts, agentLocations, messageService, activityPublisher);
        var roomService = new RoomService(_db, NullLogger<RoomService>.Instance, activityPublisher, messageService, new RoomSnapshotBuilder(_db, _catalog, new PhaseTransitionValidator(_db)), new PhaseTransitionValidator(_db));
        var roomLifecycle = new RoomLifecycleService(_db, NullLogger<RoomLifecycleService>.Instance, _catalog, activityPublisher);
        var initializationService = new InitializationService(_db, NullLogger<InitializationService>.Instance, _catalog, activityPublisher, crashRecovery, roomService, new WorkspaceRoomService(_db, NullLogger<WorkspaceRoomService>.Instance, _catalog, activityPublisher));
        var taskOrchestration = new TaskOrchestrationService(_db, NullLogger<TaskOrchestrationService>.Instance, _catalog, activityPublisher, taskLifecycle, taskQueries, roomService, new RoomSnapshotBuilder(_db, _catalog, new PhaseTransitionValidator(_db)), roomLifecycle, agentLocations, messageService, breakouts, NSubstitute.Substitute.For<AgentAcademy.Server.Services.Contracts.IWorktreeService>());
        _roomService = roomService;
        _agentLocationService = agentLocations;
        _messageService = messageService;
        _breakoutRoomService = breakouts;
        _configService = new AgentConfigService(_db);

        SeedMainRoom();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── CreateRoom ──────────────────────────────────────────

    [Fact]
    public async Task CreateRoom_ValidName_ReturnsCreated()
    {
        var controller = CreateRoomController();

        var result = await controller.CreateRoom(new CreateRoomRequest("Design Room"));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var room = Assert.IsType<RoomSnapshot>(created.Value);
        Assert.Equal("Design Room", room.Name);
        Assert.Equal(RoomStatus.Idle, room.Status);
    }

    [Fact]
    public async Task CreateRoom_WithDescription_IncludesDescriptionInWelcome()
    {
        var controller = CreateRoomController();

        var result = await controller.CreateRoom(new CreateRoomRequest("Sprint Room", "Sprint 5 planning"));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var room = Assert.IsType<RoomSnapshot>(created.Value);
        Assert.Equal("Sprint Room", room.Name);

        // Description is included in the welcome system message
        var welcomeMsg = await _db.Messages
            .Where(m => m.RoomId == room.Id && m.SenderKind == "System")
            .FirstOrDefaultAsync();
        Assert.NotNull(welcomeMsg);
        Assert.Contains("Sprint 5 planning", welcomeMsg.Content);
    }

    [Fact]
    public async Task CreateRoom_EmptyName_ReturnsBadRequest()
    {
        var controller = CreateRoomController();

        var result = await controller.CreateRoom(new CreateRoomRequest(""));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateRoom_WhitespaceName_ReturnsBadRequest()
    {
        var controller = CreateRoomController();

        var result = await controller.CreateRoom(new CreateRoomRequest("   "));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ── CreateSession ──────────────────────────────────────────

    [Fact]
    public async Task CreateSession_ValidRoom_ReturnsCreated()
    {
        var controller = CreateRoomController();

        var result = await controller.CreateSession(_mainRoomId, _sessionService);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var session = Assert.IsType<ConversationSessionSnapshot>(created.Value);
        Assert.Equal(_mainRoomId, session.RoomId);
        Assert.Equal("Active", session.Status);
    }

    [Fact]
    public async Task CreateSession_ArchivesPreviousSession()
    {
        var controller = CreateRoomController();

        // Create first session
        var result1 = await controller.CreateSession(_mainRoomId, _sessionService);
        var session1 = ((CreatedAtActionResult)result1.Result!).Value as ConversationSessionSnapshot;

        // Create second session — first should be archived
        var result2 = await controller.CreateSession(_mainRoomId, _sessionService);
        var session2 = ((CreatedAtActionResult)result2.Result!).Value as ConversationSessionSnapshot;

        Assert.NotEqual(session1!.Id, session2!.Id);

        // Verify first session is now archived
        var archivedEntity = await _db.ConversationSessions.FindAsync(session1.Id);
        Assert.Equal("Archived", archivedEntity!.Status);
    }

    [Fact]
    public async Task CreateSession_NonexistentRoom_ReturnsNotFound()
    {
        var controller = CreateRoomController();

        var result = await controller.CreateSession("nonexistent-room", _sessionService);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ── AddAgentToRoom ──────────────────────────────────────────

    [Fact]
    public async Task AddAgentToRoom_CatalogAgent_ReturnsOk()
    {
        var controller = CreateRoomController();

        var result = await controller.AddAgentToRoom(_mainRoomId, "planner", _db);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var location = Assert.IsType<AgentLocation>(ok.Value);
        Assert.Equal("planner", location.AgentId);
        Assert.Equal(_mainRoomId, location.RoomId);
    }

    [Fact]
    public async Task AddAgentToRoom_CustomAgent_ReturnsOk()
    {
        // Create a custom agent config
        await _configService.UpsertConfigAsync("my-custom",
            startupPromptOverride: "You are custom",
            modelOverride: null,
            customInstructions: """{"displayName":"My Custom","role":"Custom"}""",
            instructionTemplateId: null);

        var controller = CreateRoomController();

        var result = await controller.AddAgentToRoom(_mainRoomId, "my-custom", _db);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var location = Assert.IsType<AgentLocation>(ok.Value);
        Assert.Equal("my-custom", location.AgentId);
    }

    [Fact]
    public async Task AddAgentToRoom_PostsJoinMessage()
    {
        var controller = CreateRoomController();
        await controller.AddAgentToRoom(_mainRoomId, "planner", _db);

        var messages = await _db.Messages
            .Where(m => m.RoomId == _mainRoomId && m.Content.Contains("joined the room"))
            .ToListAsync();

        Assert.Single(messages);
        Assert.Contains("Planner", messages[0].Content);
    }

    [Fact]
    public async Task AddAgentToRoom_NonexistentRoom_ReturnsNotFound()
    {
        var controller = CreateRoomController();

        var result = await controller.AddAgentToRoom("nonexistent", "planner", _db);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddAgentToRoom_NonexistentAgent_ReturnsNotFound()
    {
        var controller = CreateRoomController();

        var result = await controller.AddAgentToRoom(_mainRoomId, "no-such-agent", _db);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ── RemoveAgentFromRoom ──────────────────────────────────────────

    [Fact]
    public async Task RemoveAgentFromRoom_CatalogAgent_MovesToMainRoom()
    {
        var controller = CreateRoomController();

        // First create a secondary room and add agent to it
        var roomResult = await controller.CreateRoom(new CreateRoomRequest("Side Room"));
        var room = (RoomSnapshot)((CreatedAtActionResult)roomResult.Result!).Value!;

        await controller.AddAgentToRoom(room.Id, "planner", _db);

        // Remove agent from secondary room
        var result = await controller.RemoveAgentFromRoom(room.Id, "planner", _db);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var location = Assert.IsType<AgentLocation>(ok.Value);
        Assert.Equal(_mainRoomId, location.RoomId);
    }

    [Fact]
    public async Task RemoveAgentFromRoom_PostsLeaveMessage()
    {
        var controller = CreateRoomController();

        var roomResult = await controller.CreateRoom(new CreateRoomRequest("Side Room"));
        var room = (RoomSnapshot)((CreatedAtActionResult)roomResult.Result!).Value!;

        await controller.AddAgentToRoom(room.Id, "planner", _db);
        await controller.RemoveAgentFromRoom(room.Id, "planner", _db);

        var messages = await _db.Messages
            .Where(m => m.RoomId == room.Id && m.Content.Contains("left the room"))
            .ToListAsync();

        Assert.Single(messages);
        Assert.Contains("Planner", messages[0].Content);
    }

    [Fact]
    public async Task RemoveAgentFromRoom_NonexistentRoom_ReturnsNotFound()
    {
        var controller = CreateRoomController();

        var result = await controller.RemoveAgentFromRoom("nonexistent", "planner", _db);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task RemoveAgentFromRoom_NonexistentAgent_ReturnsNotFound()
    {
        var controller = CreateRoomController();

        var result = await controller.RemoveAgentFromRoom(_mainRoomId, "no-such-agent", _db);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ── CreateCustomAgent ──────────────────────────────────────────

    [Fact]
    public async Task CreateCustomAgent_ValidRequest_ReturnsCreated()
    {
        var controller = CreateAgentConfigController();

        var result = await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("My Researcher", "You research things carefully."));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var agent = Assert.IsType<AgentDefinition>(created.Value);
        Assert.Equal("my-researcher", agent.Id);
        Assert.Equal("My Researcher", agent.Name);
        Assert.Equal("Custom", agent.Role);
    }

    [Fact]
    public async Task CreateCustomAgent_GeneratesKebabCaseId()
    {
        var controller = CreateAgentConfigController();

        var result = await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("Code Review Expert", "You review code."));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var agent = Assert.IsType<AgentDefinition>(created.Value);
        Assert.Equal("code-review-expert", agent.Id);
    }

    [Fact]
    public async Task CreateCustomAgent_WithModel_IncludesModel()
    {
        var controller = CreateAgentConfigController();

        var result = await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("My Agent", "Prompt", "claude-opus-4.7"));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var agent = Assert.IsType<AgentDefinition>(created.Value);
        Assert.Equal("claude-opus-4.7", agent.Model);
    }

    [Fact]
    public async Task CreateCustomAgent_PersistsConfig()
    {
        var controller = CreateAgentConfigController();

        await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("Persisted Agent", "Do things."));

        var config = await _configService.GetConfigOverrideAsync("persisted-agent");
        Assert.NotNull(config);
        Assert.Equal("Do things.", config.StartupPromptOverride);
    }

    [Fact]
    public async Task CreateCustomAgent_EmptyName_ReturnsBadRequest()
    {
        var controller = CreateAgentConfigController();

        var result = await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("", "Some prompt"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateCustomAgent_EmptyPrompt_ReturnsBadRequest()
    {
        var controller = CreateAgentConfigController();

        var result = await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("Valid Name", ""));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateCustomAgent_ConflictsWithCatalog_ReturnsConflict()
    {
        var controller = CreateAgentConfigController();

        var result = await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("Planner", "Custom planner prompt"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateCustomAgent_DuplicateCustomName_ReturnsConflict()
    {
        var controller = CreateAgentConfigController();

        await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("Unique Bot", "First version."));

        var result = await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("Unique Bot", "Second version."));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateCustomAgent_SpecialCharsInName_ProducesCleanId()
    {
        var controller = CreateAgentConfigController();

        var result = await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("My Agent!! (v2)", "Prompt"));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var agent = Assert.IsType<AgentDefinition>(created.Value);
        Assert.Equal("my-agent-v2", agent.Id);
    }

    // ── DeleteCustomAgent ──────────────────────────────────────────

    [Fact]
    public async Task DeleteCustomAgent_ExistingCustom_ReturnsOk()
    {
        var controller = CreateAgentConfigController();
        await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("Deletable Agent", "To be deleted."));

        var result = await controller.DeleteCustomAgent("deletable-agent");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task DeleteCustomAgent_RemovesFromDatabase()
    {
        var controller = CreateAgentConfigController();
        await controller.CreateCustomAgent(
            new CreateCustomAgentRequest("Gone Agent", "Will be gone."));

        await controller.DeleteCustomAgent("gone-agent");

        var config = await _configService.GetConfigOverrideAsync("gone-agent");
        Assert.Null(config);
    }

    [Fact]
    public async Task DeleteCustomAgent_BuiltInAgent_ReturnsBadRequest()
    {
        var controller = CreateAgentConfigController();

        var result = await controller.DeleteCustomAgent("planner");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task DeleteCustomAgent_NonexistentAgent_ReturnsNotFound()
    {
        var controller = CreateAgentConfigController();

        var result = await controller.DeleteCustomAgent("no-such-agent");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── Helpers ──────────────────────────────────────────

    private void SeedMainRoom()
    {
        _db.Rooms.Add(new RoomEntity
        {
            Id = _mainRoomId,
            Name = "Main Collaboration Room",
            Status = "Active",
            CurrentPhase = "Discussion",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    private RoomController CreateRoomController()
    {
        var logger = Substitute.For<ILogger<RoomController>>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var usageTracker = new LlmUsageTracker(scopeFactory, NullLogger<LlmUsageTracker>.Instance);
        var errorTracker = new AgentErrorTracker(scopeFactory, NullLogger<AgentErrorTracker>.Instance);
        var activityPublisher = new ActivityPublisher(_db, new ActivityBroadcaster());
        var artifactTracker = new RoomArtifactTracker(_db, activityPublisher, NullLogger<RoomArtifactTracker>.Instance);
        var evaluator = new ArtifactEvaluatorService(_db, NullLogger<ArtifactEvaluatorService>.Instance);
        return new RoomController(_roomService, _agentLocationService, _messageService, new MessageBroadcaster(), _catalog, usageTracker, errorTracker, artifactTracker, evaluator, logger);
    }

    private AgentController CreateAgentController()
    {
        var logger = Substitute.For<ILogger<AgentController>>();
        var executor = Substitute.For<IAgentExecutor>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var usageTracker = new LlmUsageTracker(scopeFactory, NullLogger<LlmUsageTracker>.Instance);
        var quotaService = new AgentQuotaService(scopeFactory, usageTracker, NullLogger<AgentQuotaService>.Instance);
        return new AgentController(_agentLocationService, _breakoutRoomService, executor, _catalog, quotaService, logger);
    }

    private AgentConfigController CreateAgentConfigController()
    {
        var logger = Substitute.For<ILogger<AgentConfigController>>();
        return new AgentConfigController(_catalog, _configService, logger);
    }
}
