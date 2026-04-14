using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class LlmUsageTrackerBasicTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly LlmUsageTracker _tracker;

    public LlmUsageTrackerBasicTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options =>
            options.UseSqlite(_connection));
        services.AddSingleton<ILogger<LlmUsageTracker>>(NullLogger<LlmUsageTracker>.Instance);
        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
        }

        _tracker = new LlmUsageTracker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LlmUsageTracker>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RecordAsync_PersistsUsageEntity()
    {
        await _tracker.RecordAsync(
            agentId: "agent-1",
            roomId: "room-1",
            model: "gpt-5",
            inputTokens: 100,
            outputTokens: 50,
            cacheReadTokens: 20,
            cacheWriteTokens: 10,
            cost: 0.005,
            durationMs: 1234,
            apiCallId: "chatcmpl-abc",
            initiator: null,
            reasoningEffort: "medium");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var records = await db.LlmUsage.ToListAsync();

        Assert.Single(records);
        var r = records[0];
        Assert.Equal("agent-1", r.AgentId);
        Assert.Equal("room-1", r.RoomId);
        Assert.Equal("gpt-5", r.Model);
        Assert.Equal(100, r.InputTokens);
        Assert.Equal(50, r.OutputTokens);
        Assert.Equal(20, r.CacheReadTokens);
        Assert.Equal(10, r.CacheWriteTokens);
        Assert.Equal(0.005, r.Cost);
        Assert.Equal(1234, r.DurationMs);
        Assert.Equal("chatcmpl-abc", r.ApiCallId);
        Assert.Equal("medium", r.ReasoningEffort);
        Assert.NotEqual(default, r.RecordedAt);
    }

    [Fact]
    public async Task RecordAsync_NullTokens_DefaultsToZero()
    {
        await _tracker.RecordAsync(
            agentId: "agent-1", roomId: null,
            model: null,
            inputTokens: null, outputTokens: null,
            cacheReadTokens: null, cacheWriteTokens: null,
            cost: null, durationMs: null,
            apiCallId: null, initiator: null,
            reasoningEffort: null);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var r = await db.LlmUsage.SingleAsync();

        Assert.Equal(0, r.InputTokens);
        Assert.Equal(0, r.OutputTokens);
        Assert.Null(r.Cost);
        Assert.Null(r.DurationMs);
    }

    [Fact]
    public async Task RecordAsync_DbFailure_DoesNotThrow()
    {
        // Use a broken scope factory to simulate DB failure
        var brokenScopeFactory = Substitute.For<IServiceScopeFactory>();
        brokenScopeFactory.CreateScope().Returns(_ => throw new InvalidOperationException("DB down"));

        var tracker = new LlmUsageTracker(brokenScopeFactory, NullLogger<LlmUsageTracker>.Instance);

        // Should not throw
        await tracker.RecordAsync(
            "agent-1", "room-1", "gpt-5",
            100, 50, 0, 0, 0.01, 500,
            null, null, null);
    }

    [Fact]
    public async Task GetRoomUsageAsync_AggregatesCorrectly()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);
        await SeedUsage("agent-2", "room-1", "claude-4", 200, 100, 0.02);
        await SeedUsage("agent-1", "room-2", "gpt-5", 300, 150, 0.03);

        var usage = await _tracker.GetRoomUsageAsync("room-1");

        Assert.Equal(300, usage.TotalInputTokens);
        Assert.Equal(150, usage.TotalOutputTokens);
        Assert.Equal(0.03, usage.TotalCost);
        Assert.Equal(2, usage.RequestCount);
        Assert.Contains("gpt-5", usage.Models);
        Assert.Contains("claude-4", usage.Models);
    }

    [Fact]
    public async Task GetRoomUsageAsync_EmptyRoom_ReturnsZeroes()
    {
        var usage = await _tracker.GetRoomUsageAsync("nonexistent-room");

        Assert.Equal(0, usage.TotalInputTokens);
        Assert.Equal(0, usage.TotalOutputTokens);
        Assert.Equal(0.0, usage.TotalCost);
        Assert.Equal(0, usage.RequestCount);
        Assert.Empty(usage.Models);
    }

    [Fact]
    public async Task GetRoomUsageByAgentAsync_GroupsCorrectly()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);
        await SeedUsage("agent-1", "room-1", "gpt-5", 200, 100, 0.02);
        await SeedUsage("agent-2", "room-1", "claude-4", 150, 75, 0.015);

        var breakdown = await _tracker.GetRoomUsageByAgentAsync("room-1");

        Assert.Equal(2, breakdown.Count);

        var a1 = breakdown.Single(b => b.AgentId == "agent-1");
        Assert.Equal(300, a1.TotalInputTokens);
        Assert.Equal(150, a1.TotalOutputTokens);
        Assert.Equal(2, a1.RequestCount);

        var a2 = breakdown.Single(b => b.AgentId == "agent-2");
        Assert.Equal(150, a2.TotalInputTokens);
        Assert.Equal(75, a2.TotalOutputTokens);
        Assert.Equal(1, a2.RequestCount);
    }

    [Fact]
    public async Task GetGlobalUsageAsync_AggregatesAcrossRooms()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);
        await SeedUsage("agent-1", "room-2", "gpt-5", 200, 100, 0.02);

        var usage = await _tracker.GetGlobalUsageAsync();

        Assert.Equal(300, usage.TotalInputTokens);
        Assert.Equal(150, usage.TotalOutputTokens);
        Assert.Equal(0.03, usage.TotalCost);
        Assert.Equal(2, usage.RequestCount);
    }

    [Fact]
    public async Task GetGlobalUsageAsync_WithSinceFilter_ExcludesOldRecords()
    {
        // Seed one old and one new record
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.LlmUsage.Add(new LlmUsageEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                AgentId = "agent-1", RoomId = "room-1",
                Model = "gpt-5",
                InputTokens = 100, OutputTokens = 50,
                RecordedAt = DateTime.UtcNow.AddHours(-48)
            });
            db.LlmUsage.Add(new LlmUsageEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                AgentId = "agent-1", RoomId = "room-1",
                Model = "gpt-5",
                InputTokens = 200, OutputTokens = 100,
                RecordedAt = DateTime.UtcNow.AddHours(-1)
            });
            await db.SaveChangesAsync();
        }

        var usage = await _tracker.GetGlobalUsageAsync(since: DateTime.UtcNow.AddHours(-24));

        Assert.Equal(200, usage.TotalInputTokens);
        Assert.Equal(100, usage.TotalOutputTokens);
        Assert.Equal(1, usage.RequestCount);
    }

    [Fact]
    public async Task GetRecentUsageAsync_ReturnsInReverseChronologicalOrder()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);
        await Task.Delay(10); // ensure different timestamps
        await SeedUsage("agent-1", "room-1", "gpt-5", 200, 100, 0.02);

        var records = await _tracker.GetRecentUsageAsync(roomId: "room-1");

        Assert.Equal(2, records.Count);
        Assert.True(records[0].RecordedAt >= records[1].RecordedAt);
        Assert.Equal(200, records[0].InputTokens);
        Assert.Equal(100, records[1].InputTokens);
    }

    [Fact]
    public async Task GetRecentUsageAsync_FiltersByAgent()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);
        await SeedUsage("agent-2", "room-1", "gpt-5", 200, 100, 0.02);

        var records = await _tracker.GetRecentUsageAsync(roomId: "room-1", agentId: "agent-1");

        Assert.Single(records);
        Assert.Equal("agent-1", records[0].AgentId);
    }

    [Fact]
    public async Task GetRecentUsageAsync_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            await SeedUsage("agent-1", "room-1", "gpt-5", i * 100, i * 50, 0.01);

        var records = await _tracker.GetRecentUsageAsync(roomId: "room-1", limit: 3);

        Assert.Equal(3, records.Count);
    }

    [Fact]
    public async Task GetRecentUsageAsync_GlobalQuery_AllRooms()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);
        await SeedUsage("agent-1", "room-2", "gpt-5", 200, 100, 0.02);

        var records = await _tracker.GetRecentUsageAsync(roomId: null);

        Assert.Equal(2, records.Count);
    }

    private async Task SeedUsage(string agentId, string roomId, string model,
        double input, double output, double cost)
    {
        await _tracker.RecordAsync(
            agentId, roomId, model,
            input, output, 0, 0,
            cost, 500, null, null, null);
    }
}

public class UsageApiEndpointTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly LlmUsageTracker _tracker;
    private readonly AgentAcademyDbContext _db;
    private readonly RoomService _roomService;
    private readonly AgentLocationService _agentLocationService;
    private readonly BreakoutRoomService _breakoutRoomService;
    private readonly MessageService _messageService;
    private readonly ActivityPublisher _activityPublisher;
    private readonly AgentCatalogOptions _catalog;

    public UsageApiEndpointTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options =>
            options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        _db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        _db.Database.EnsureCreated();

        _tracker = new LlmUsageTracker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LlmUsageTracker>.Instance);

        _catalog = new AgentCatalogOptions("main", "Main Room", new List<AgentDefinition>());
        var executor = Substitute.For<IAgentExecutor>();
        var sessionService = new ConversationSessionService(
            _db, new SystemSettingsService(_db), executor,
            NullLogger<ConversationSessionService>.Instance);
        var activityBus = new ActivityBroadcaster();
        var activityPublisher = new ActivityPublisher(_db, activityBus);
        var taskDeps = new TaskDependencyService(_db, NullLogger<TaskDependencyService>.Instance, activityPublisher);
        var taskQueries = new TaskQueryService(_db, NullLogger<TaskQueryService>.Instance, _catalog, taskDeps);
        var taskLifecycle = new TaskLifecycleService(_db, NullLogger<TaskLifecycleService>.Instance, _catalog, activityPublisher, taskDeps);
        var agentLocations = new AgentLocationService(_db, _catalog, activityPublisher);
        var planService = new PlanService(_db);
        var messageService = new MessageService(_db, NullLogger<MessageService>.Instance, _catalog, activityPublisher, sessionService, new MessageBroadcaster());
        var breakouts = new BreakoutRoomService(_db, NullLogger<BreakoutRoomService>.Instance, _catalog, activityPublisher, sessionService, taskQueries, agentLocations);
        var crashRecovery = new CrashRecoveryService(_db, NullLogger<CrashRecoveryService>.Instance, breakouts, agentLocations, messageService, activityPublisher);
        var roomService = new RoomService(_db, NullLogger<RoomService>.Instance, activityPublisher, messageService, new RoomSnapshotBuilder(_db, _catalog, new PhaseTransitionValidator(_db)), new PhaseTransitionValidator(_db));
        var roomLifecycle = new RoomLifecycleService(_db, NullLogger<RoomLifecycleService>.Instance, _catalog, activityPublisher);
        var initializationService = new InitializationService(_db, NullLogger<InitializationService>.Instance, _catalog, activityPublisher, crashRecovery, roomService, new WorkspaceRoomService(_db, NullLogger<WorkspaceRoomService>.Instance, _catalog, activityPublisher));
        var taskOrchestration = new TaskOrchestrationService(_db, NullLogger<TaskOrchestrationService>.Instance, _catalog, activityPublisher, taskLifecycle, taskQueries, roomService, new RoomSnapshotBuilder(_db, _catalog, new PhaseTransitionValidator(_db)), roomLifecycle, agentLocations, messageService, breakouts);
        _activityPublisher = activityPublisher;
        _roomService = roomService;
        _agentLocationService = agentLocations;
        _breakoutRoomService = breakouts;
        _messageService = messageService;
    }

    public void Dispose()
    {
        _db.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RoomUsageEndpoint_ReturnsAggregatedData()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);
        await SeedUsage("agent-2", "room-1", "claude-4", 200, 100, 0.02);

        var controller = new RoomController(
            _roomService, _agentLocationService, _messageService, new MessageBroadcaster(), _catalog, _tracker,
            new AgentErrorTracker(_serviceProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentErrorTracker>.Instance),
            new RoomArtifactTracker(_db, _activityPublisher, NullLogger<RoomArtifactTracker>.Instance),
            new ArtifactEvaluatorService(_db, NullLogger<ArtifactEvaluatorService>.Instance),
            NullLogger<RoomController>.Instance);

        var result = await controller.GetRoomUsage("room-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var usage = Assert.IsType<UsageSummary>(ok.Value);

        Assert.Equal(300, usage.TotalInputTokens);
        Assert.Equal(150, usage.TotalOutputTokens);
        Assert.Equal(2, usage.RequestCount);
    }

    [Fact]
    public async Task RoomUsageByAgentEndpoint_ReturnsBreakdown()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);
        await SeedUsage("agent-2", "room-1", "gpt-5", 200, 100, 0.02);

        var controller = new RoomController(
            _roomService, _agentLocationService, _messageService, new MessageBroadcaster(), _catalog, _tracker,
            new AgentErrorTracker(_serviceProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentErrorTracker>.Instance),
            new RoomArtifactTracker(_db, _activityPublisher, NullLogger<RoomArtifactTracker>.Instance),
            new ArtifactEvaluatorService(_db, NullLogger<ArtifactEvaluatorService>.Instance),
            NullLogger<RoomController>.Instance);
        var result = await controller.GetRoomUsageByAgent("room-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var breakdown = Assert.IsType<List<AgentUsageSummary>>(ok.Value);

        Assert.Equal(2, breakdown.Count);
    }

    [Fact]
    public async Task RoomUsageRecordsEndpoint_ReturnsRecords()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);

        var controller = new RoomController(
            _roomService, _agentLocationService, _messageService, new MessageBroadcaster(), _catalog, _tracker,
            new AgentErrorTracker(_serviceProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentErrorTracker>.Instance),
            new RoomArtifactTracker(_db, _activityPublisher, NullLogger<RoomArtifactTracker>.Instance),
            new ArtifactEvaluatorService(_db, NullLogger<ArtifactEvaluatorService>.Instance),
            NullLogger<RoomController>.Instance);
        var result = await controller.GetRoomUsageRecords("room-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var records = Assert.IsType<List<LlmUsageRecord>>(ok.Value);

        Assert.Single(records);
        Assert.Equal("agent-1", records[0].AgentId);
    }

    [Fact]
    public async Task RoomUsageRecordsEndpoint_ClampsLimit()
    {
        for (int i = 0; i < 5; i++)
            await SeedUsage("agent-1", "room-1", "gpt-5", i * 100, i * 50, 0.01);

        var controller = new RoomController(
            _roomService, _agentLocationService, _messageService, new MessageBroadcaster(), _catalog, _tracker,
            new AgentErrorTracker(_serviceProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentErrorTracker>.Instance),
            new RoomArtifactTracker(_db, _activityPublisher, NullLogger<RoomArtifactTracker>.Instance),
            new ArtifactEvaluatorService(_db, NullLogger<ArtifactEvaluatorService>.Instance),
            NullLogger<RoomController>.Instance);
        var result = await controller.GetRoomUsageRecords("room-1", limit: 2);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var records = Assert.IsType<List<LlmUsageRecord>>(ok.Value);

        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task GlobalUsageEndpoint_ReturnsAcrossAllRooms()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);
        await SeedUsage("agent-1", "room-2", "claude-4", 200, 100, 0.02);

        var controller = CreateSystemController();
        var result = await controller.GetGlobalUsage();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var usage = Assert.IsType<UsageSummary>(ok.Value);

        Assert.Equal(300, usage.TotalInputTokens);
        Assert.Equal(150, usage.TotalOutputTokens);
        Assert.Equal(2, usage.RequestCount);
    }

    [Fact]
    public async Task GlobalUsageEndpoint_WithHoursBack_FiltersOldRecords()
    {
        _db.LlmUsage.Add(new LlmUsageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            AgentId = "agent-1", RoomId = "room-1",
            Model = "gpt-5", InputTokens = 100, OutputTokens = 50,
            RecordedAt = DateTime.UtcNow.AddHours(-48)
        });
        await _db.SaveChangesAsync();

        await SeedUsage("agent-1", "room-1", "gpt-5", 200, 100, 0.02);

        var controller = CreateSystemController();
        var result = await controller.GetGlobalUsage(hoursBack: 24);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var usage = Assert.IsType<UsageSummary>(ok.Value);

        Assert.Equal(200, usage.TotalInputTokens);
        Assert.Equal(1, usage.RequestCount);
    }

    [Fact]
    public async Task GlobalUsageRecordsEndpoint_ReturnsRecords()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);

        var controller = CreateSystemController();
        var result = await controller.GetGlobalUsageRecords();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var records = Assert.IsType<List<LlmUsageRecord>>(ok.Value);

        Assert.Single(records);
    }

    [Fact]
    public async Task GlobalUsageRecordsEndpoint_FiltersByAgent()
    {
        await SeedUsage("agent-1", "room-1", "gpt-5", 100, 50, 0.01);
        await SeedUsage("agent-2", "room-1", "gpt-5", 200, 100, 0.02);

        var controller = CreateSystemController();
        var result = await controller.GetGlobalUsageRecords(agentId: "agent-2");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var records = Assert.IsType<List<LlmUsageRecord>>(ok.Value);

        Assert.Single(records);
        Assert.Equal("agent-2", records[0].AgentId);
    }

    private SystemController CreateSystemController()
    {
        var executor = Substitute.For<IAgentExecutor>();

        var errorTracker = new AgentErrorTracker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentErrorTracker>.Instance);

        return new SystemController(
            _roomService, _agentLocationService, _breakoutRoomService, _activityPublisher, executor, _catalog, _db, _tracker, errorTracker,
            NullLogger<SystemController>.Instance);
    }

    private async Task SeedUsage(string agentId, string roomId, string model,
        double input, double output, double cost)
    {
        await _tracker.RecordAsync(
            agentId, roomId, model,
            input, output, 0, 0,
            cost, 500, null, null, null);
    }
}
