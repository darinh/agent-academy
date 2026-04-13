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

public class AgentErrorTrackerBasicTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentErrorTracker _tracker;

    public AgentErrorTrackerBasicTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options =>
            options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
        }

        _tracker = new AgentErrorTracker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentErrorTracker>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RecordAsync_PersistsErrorEntity()
    {
        await _tracker.RecordAsync(
            agentId: "agent-1",
            roomId: "room-1",
            errorType: "authentication",
            message: "Token expired",
            recoverable: false);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var errors = await db.AgentErrors.ToListAsync();

        Assert.Single(errors);
        var e = errors[0];
        Assert.Equal("agent-1", e.AgentId);
        Assert.Equal("room-1", e.RoomId);
        Assert.Equal("authentication", e.ErrorType);
        Assert.Equal("Token expired", e.Message);
        Assert.False(e.Recoverable);
        Assert.False(e.Retried);
        Assert.Null(e.RetryAttempt);
    }

    [Fact]
    public async Task RecordAsync_PersistsRetryInfo()
    {
        await _tracker.RecordAsync(
            agentId: "agent-1",
            roomId: "room-1",
            errorType: "quota",
            message: "Rate limit exceeded",
            recoverable: true,
            retried: true,
            retryAttempt: 2);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var e = await db.AgentErrors.SingleAsync();

        Assert.True(e.Recoverable);
        Assert.True(e.Retried);
        Assert.Equal(2, e.RetryAttempt);
    }

    [Fact]
    public async Task RecordAsync_TruncatesLongMessages()
    {
        var longMessage = new string('x', 3000);
        await _tracker.RecordAsync("agent-1", "room-1", "transient", longMessage, true);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var e = await db.AgentErrors.SingleAsync();

        Assert.True(e.Message.Length <= 2001); // 2000 + "…"
        Assert.EndsWith("…", e.Message);
    }

    [Fact]
    public async Task RecordAsync_HandlesNullRoomId()
    {
        await _tracker.RecordAsync("agent-1", null, "unknown", "Some error", true);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var e = await db.AgentErrors.SingleAsync();

        Assert.Null(e.RoomId);
    }

    [Fact]
    public async Task GetRoomErrorsAsync_ReturnsErrorsForRoom()
    {
        await _tracker.RecordAsync("agent-1", "room-1", "auth", "Error 1", false);
        await _tracker.RecordAsync("agent-2", "room-1", "quota", "Error 2", true);
        await _tracker.RecordAsync("agent-1", "room-2", "transient", "Error 3", true);

        var errors = await _tracker.GetRoomErrorsAsync("room-1");

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal("room-1", e.RoomId));
    }

    [Fact]
    public async Task GetRoomErrorsAsync_RespectsLimit()
    {
        for (int i = 0; i < 5; i++)
            await _tracker.RecordAsync("agent-1", "room-1", "transient", $"Error {i}", true);

        var errors = await _tracker.GetRoomErrorsAsync("room-1", limit: 3);

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public async Task GetRoomErrorsAsync_ReturnsEmpty_WhenNoErrors()
    {
        var errors = await _tracker.GetRoomErrorsAsync("nonexistent");
        Assert.Empty(errors);
    }

    [Fact]
    public async Task GetRecentErrorsAsync_ReturnsAllErrors()
    {
        await _tracker.RecordAsync("agent-1", "room-1", "auth", "Error 1", false);
        await _tracker.RecordAsync("agent-2", "room-2", "quota", "Error 2", true);

        var errors = await _tracker.GetRecentErrorsAsync();

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public async Task GetRecentErrorsAsync_FiltersByAgent()
    {
        await _tracker.RecordAsync("agent-1", "room-1", "auth", "Error 1", false);
        await _tracker.RecordAsync("agent-2", "room-2", "quota", "Error 2", true);

        var errors = await _tracker.GetRecentErrorsAsync(agentId: "agent-1");

        Assert.Single(errors);
        Assert.Equal("agent-1", errors[0].AgentId);
    }

    [Fact]
    public async Task GetRecentErrorsAsync_FiltersBySince()
    {
        await _tracker.RecordAsync("agent-1", "room-1", "auth", "Old error", false);

        // The error we just recorded should be recent enough
        var errors = await _tracker.GetRecentErrorsAsync(since: DateTime.UtcNow.AddMinutes(-1));
        Assert.Single(errors);

        var noErrors = await _tracker.GetRecentErrorsAsync(since: DateTime.UtcNow.AddMinutes(1));
        Assert.Empty(noErrors);
    }

    [Fact]
    public async Task GetErrorSummaryAsync_ReturnsAggregatedData()
    {
        await _tracker.RecordAsync("agent-1", "room-1", "authentication", "Auth fail", false);
        await _tracker.RecordAsync("agent-1", "room-1", "quota", "Rate limited", true);
        await _tracker.RecordAsync("agent-2", "room-2", "transient", "Network error", true);

        var summary = await _tracker.GetErrorSummaryAsync();

        Assert.Equal(3, summary.TotalErrors);
        Assert.Equal(2, summary.RecoverableErrors);
        Assert.Equal(1, summary.UnrecoverableErrors);
        Assert.Equal(3, summary.ByType.Count);
        Assert.Equal(2, summary.ByAgent.Count);
    }

    [Fact]
    public async Task GetErrorSummaryAsync_ReturnsEmpty_WhenNoErrors()
    {
        var summary = await _tracker.GetErrorSummaryAsync();

        Assert.Equal(0, summary.TotalErrors);
        Assert.Equal(0, summary.RecoverableErrors);
        Assert.Equal(0, summary.UnrecoverableErrors);
        Assert.Empty(summary.ByType);
        Assert.Empty(summary.ByAgent);
    }
}

public class ErrorApiEndpointTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentErrorTracker _errorTracker;
    private readonly AgentAcademyDbContext _db;
    private readonly RoomService _roomService;
    private readonly AgentLocationService _agentLocationService;
    private readonly BreakoutRoomService _breakoutRoomService;
    private readonly ActivityPublisher _activityPublisher;
    private readonly AgentCatalogOptions _catalog;

    public ErrorApiEndpointTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options =>
            options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        _db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        _db.Database.EnsureCreated();

        _errorTracker = new AgentErrorTracker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentErrorTracker>.Instance);

        _catalog = new AgentCatalogOptions("main", "Main Room", new List<AgentDefinition>());
        var executor = Substitute.For<IAgentExecutor>();
        var sessionService = new ConversationSessionService(
            _db, new SystemSettingsService(_db), executor,
            NullLogger<ConversationSessionService>.Instance);
        var taskQueries = new TaskQueryService(_db, NullLogger<TaskQueryService>.Instance, _catalog);
        var activityBus = new ActivityBroadcaster();
        var activityPublisher = new ActivityPublisher(_db, activityBus);
        var taskLifecycle = new TaskLifecycleService(_db, NullLogger<TaskLifecycleService>.Instance, _catalog, activityPublisher);
        var agentLocations = new AgentLocationService(_db, _catalog, activityPublisher);
        var planService = new PlanService(_db);
        var messageService = new MessageService(_db, NullLogger<MessageService>.Instance, _catalog, activityPublisher, sessionService);
        var breakouts = new BreakoutRoomService(_db, NullLogger<BreakoutRoomService>.Instance, _catalog, activityPublisher, sessionService, taskQueries, agentLocations);
        var crashRecovery = new CrashRecoveryService(_db, NullLogger<CrashRecoveryService>.Instance, breakouts, agentLocations, messageService, activityPublisher);
        var roomService = new RoomService(_db, NullLogger<RoomService>.Instance, activityPublisher, messageService, new RoomSnapshotBuilder(_db, _catalog));
        var roomLifecycle = new RoomLifecycleService(_db, NullLogger<RoomLifecycleService>.Instance, _catalog, activityPublisher);
        var initializationService = new InitializationService(_db, NullLogger<InitializationService>.Instance, _catalog, activityPublisher, crashRecovery, roomService, new WorkspaceRoomService(_db, NullLogger<WorkspaceRoomService>.Instance, _catalog, activityPublisher));
        var taskOrchestration = new TaskOrchestrationService(_db, NullLogger<TaskOrchestrationService>.Instance, _catalog, activityPublisher, taskLifecycle, roomService, new RoomSnapshotBuilder(_db, _catalog), roomLifecycle, agentLocations, messageService, breakouts);
        _activityPublisher = activityPublisher;
        _roomService = roomService;
        _agentLocationService = agentLocations;
        _breakoutRoomService = breakouts;
    }

    public void Dispose()
    {
        _db.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RoomErrorsEndpoint_ReturnsErrorsForRoom()
    {
        await _errorTracker.RecordAsync("agent-1", "room-1", "auth", "Auth fail", false);
        await _errorTracker.RecordAsync("agent-2", "room-1", "quota", "Rate limit", true);

        var controller = CreateRoomController();
        var result = await controller.GetRoomErrors("room-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var errors = Assert.IsType<List<ErrorRecord>>(ok.Value);

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public async Task RoomErrorsEndpoint_ReturnsEmpty_WhenNoErrors()
    {
        var controller = CreateRoomController();
        var result = await controller.GetRoomErrors("empty-room");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var errors = Assert.IsType<List<ErrorRecord>>(ok.Value);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task GlobalErrorSummaryEndpoint_ReturnsAggregatedData()
    {
        await _errorTracker.RecordAsync("agent-1", "room-1", "auth", "Auth fail", false);
        await _errorTracker.RecordAsync("agent-2", "room-2", "transient", "Network error", true);

        var controller = CreateSystemController();
        var result = await controller.GetGlobalErrorSummary();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<ErrorSummary>(ok.Value);

        Assert.Equal(2, summary.TotalErrors);
        Assert.Equal(1, summary.RecoverableErrors);
        Assert.Equal(1, summary.UnrecoverableErrors);
    }

    [Fact]
    public async Task GlobalErrorSummaryEndpoint_ValidatesHoursBack()
    {
        var controller = CreateSystemController();
        var result = await controller.GetGlobalErrorSummary(hoursBack: 0);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GlobalErrorRecordsEndpoint_ReturnsRecords()
    {
        await _errorTracker.RecordAsync("agent-1", "room-1", "auth", "Auth fail", false);

        var controller = CreateSystemController();
        var result = await controller.GetGlobalErrorRecords();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var records = Assert.IsType<List<ErrorRecord>>(ok.Value);

        Assert.Single(records);
        Assert.Equal("agent-1", records[0].AgentId);
    }

    [Fact]
    public async Task GlobalErrorRecordsEndpoint_FiltersByAgent()
    {
        await _errorTracker.RecordAsync("agent-1", "room-1", "auth", "Error 1", false);
        await _errorTracker.RecordAsync("agent-2", "room-2", "quota", "Error 2", true);

        var controller = CreateSystemController();
        var result = await controller.GetGlobalErrorRecords(agentId: "agent-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var records = Assert.IsType<List<ErrorRecord>>(ok.Value);

        Assert.Single(records);
        Assert.Equal("agent-1", records[0].AgentId);
    }

    [Fact]
    public async Task GlobalErrorRecordsEndpoint_ValidatesHoursBack()
    {
        var controller = CreateSystemController();
        var result = await controller.GetGlobalErrorRecords(hoursBack: 99999);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private RoomController CreateRoomController()
    {
        var usageTracker = new LlmUsageTracker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LlmUsageTracker>.Instance);
        return new RoomController(
            _roomService, _agentLocationService,
            new MessageService(_db, NullLogger<MessageService>.Instance, _catalog, _activityPublisher,
                new ConversationSessionService(_db, new SystemSettingsService(_db),
                    Substitute.For<IAgentExecutor>(), NullLogger<ConversationSessionService>.Instance)),
            _catalog, usageTracker, _errorTracker,
            NullLogger<RoomController>.Instance);
    }

    private SystemController CreateSystemController()
    {
        var executor = Substitute.For<IAgentExecutor>();
        var usageTracker = new LlmUsageTracker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LlmUsageTracker>.Instance);
        return new SystemController(
            _roomService, _agentLocationService, _breakoutRoomService, _activityPublisher,
            executor, _catalog, _db, usageTracker, _errorTracker,
            NullLogger<SystemController>.Instance);
    }
}
