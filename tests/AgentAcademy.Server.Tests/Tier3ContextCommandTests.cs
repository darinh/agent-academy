using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for Tier 3B Context command handlers:
/// HandoffSummaryHandler, PlatformStatusHandler.
/// </summary>
public sealed class Tier3ContextCommandTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    public Tier3ContextCommandTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Backend engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: ["coding"], EnabledTools: ["code", "code-write"],
                    AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(
                        ["LIST_*", "HANDOFF_SUMMARY", "PLATFORM_STATUS", "WHOAMI"], [])),
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: ["planning"], EnabledTools: ["chat"],
                    AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["*"], []))
            ]
        );

        var executorMock = Substitute.For<IAgentExecutor>();
        executorMock.IsFullyOperational.Returns(true);
        executorMock.IsAuthFailed.Returns(false);
        executorMock.CircuitBreakerState.Returns(CircuitState.Closed);

        var sprintServiceMock = Substitute.For<ISprintService>();
        sprintServiceMock.GetActiveSprintAsync(Arg.Any<string>()).Returns((SprintEntity?)null);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton<IMessageBroadcaster>(sp => sp.GetRequiredService<MessageBroadcaster>());
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<MessageService>();
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddScoped<AgentLocationService>();
        services.AddScoped<IAgentLocationService>(sp => sp.GetRequiredService<AgentLocationService>());
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddScoped<IBreakoutRoomService>(sp => sp.GetRequiredService<BreakoutRoomService>());
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<IPhaseTransitionValidator>(sp => sp.GetRequiredService<PhaseTransitionValidator>());
        services.AddScoped<RoomService>();
        services.AddScoped<IRoomService>(sp => sp.GetRequiredService<RoomService>());
        services.AddScoped<RoomSnapshotBuilder>();
        services.AddScoped<IRoomSnapshotBuilder>(sp => sp.GetRequiredService<RoomSnapshotBuilder>());
        services.AddSingleton<ILogger<WorkspaceRoomService>>(NullLogger<WorkspaceRoomService>.Instance);
        services.AddScoped<WorkspaceRoomService>();
        services.AddScoped<IWorkspaceRoomService>(sp => sp.GetRequiredService<WorkspaceRoomService>());
        services.AddSingleton<ILogger<RoomLifecycleService>>(NullLogger<RoomLifecycleService>.Instance);
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<IRoomLifecycleService>(sp => sp.GetRequiredService<RoomLifecycleService>());
        services.AddScoped<CrashRecoveryService>();
        services.AddScoped<ICrashRecoveryService>(sp => sp.GetRequiredService<CrashRecoveryService>());
        services.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        services.AddScoped<InitializationService>();
        services.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        services.AddScoped<TaskOrchestrationService>();
        services.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
        services.AddSingleton<IAgentExecutor>(executorMock);
        services.AddSingleton<AgentAcademy.Server.Services.AgentWatchdog.IWatchdogAgentRunner>(sp =>
            new TestDoubles.NoOpWatchdogAgentRunner(sp.GetRequiredService<IAgentExecutor>()));
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<IConversationSessionService>(sp => sp.GetRequiredService<ConversationSessionService>());
        services.AddScoped<TaskEvidenceService>();
        services.AddScoped<ITaskEvidenceService>(sp => sp.GetRequiredService<TaskEvidenceService>());
        services.AddSingleton<ISprintService>(sprintServiceMock);
        services.AddSingleton<SignalRConnectionTracker>();
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        string commandName,
        Dictionary<string, object?> args,
        string agentId = "engineer-1",
        string agentName = "Hephaestus",
        string agentRole = "SoftwareEngineer",
        string? workingDirectory = null)
    {
        var scope = _serviceProvider.CreateScope();
        var command = new CommandEnvelope(
            Command: commandName,
            Args: args,
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: agentId
        );
        var context = new CommandContext(
            AgentId: agentId,
            AgentName: agentName,
            AgentRole: agentRole,
            RoomId: "main",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider,
            WorkingDirectory: workingDirectory
        );
        return (command, context);
    }

    private async Task<string> CreateTask(string title = "Test Task", string assigneeId = "engineer-1")
    {
        using var scope = _serviceProvider.CreateScope();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var result = await taskOrchestration.CreateTaskAsync(new TaskAssignmentRequest(
            Title: title,
            Description: "A test task",
            SuccessCriteria: "Tests pass",
            RoomId: null,
            PreferredRoles: ["SoftwareEngineer"]
        ));
        return result.Task.Id;
    }

    private async Task SeedMemories(string agentId, int count = 5)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        for (int i = 0; i < count; i++)
        {
            db.AgentMemories.Add(new AgentMemoryEntity
            {
                AgentId = agentId,
                Category = "pattern",
                Key = $"memory-{i}",
                Value = $"Learned pattern #{i}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-count + i),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-count + i)
            });
        }
        await db.SaveChangesAsync();
    }

    // ════════════════════════════════════════════════════════════════
    // Handler discovery & properties
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void HandoffSummary_HasCorrectCommandName()
    {
        Assert.Equal("HANDOFF_SUMMARY", new HandoffSummaryHandler().CommandName);
    }

    [Fact]
    public void PlatformStatus_HasCorrectCommandName()
    {
        Assert.Equal("PLATFORM_STATUS", new PlatformStatusHandler().CommandName);
    }

    [Fact]
    public void BothHandlers_AreRetrySafe()
    {
        Assert.True(new HandoffSummaryHandler().IsRetrySafe);
        Assert.True(new PlatformStatusHandler().IsRetrySafe);
    }

    [Fact]
    public void BothHandlers_AreNotDestructive()
    {
        ICommandHandler handoff = new HandoffSummaryHandler();
        ICommandHandler platform = new PlatformStatusHandler();
        Assert.False(handoff.IsDestructive);
        Assert.False(platform.IsDestructive);
    }

    // ════════════════════════════════════════════════════════════════
    // HANDOFF_SUMMARY
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HandoffSummary_ReturnsAgentIdentity()
    {
        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;

        var agent = (Dictionary<string, object?>)dict["agent"]!;
        Assert.Equal("engineer-1", agent["id"]);
        Assert.Equal("Hephaestus", agent["name"]);
        Assert.Equal("SoftwareEngineer", agent["role"]);
    }

    [Fact]
    public async Task HandoffSummary_ReturnsLocation_FallsBackToContext()
    {
        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new(),
            workingDirectory: "/home/test/projects/agent-academy");
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var location = (Dictionary<string, object?>)dict["location"]!;

        Assert.Equal("main", location["roomId"]);
        Assert.Equal("/home/test/projects/agent-academy", location["workingDirectory"]);
    }

    [Fact]
    public async Task HandoffSummary_WithNoTasks_ReturnsEmptyList()
    {
        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;

        var tasks = (List<Dictionary<string, object?>>)dict["assignedTasks"]!;
        Assert.Empty(tasks);
        Assert.Equal(0, (int)dict["assignedTaskCount"]!);
    }

    [Fact]
    public async Task HandoffSummary_WithAssignedTask_IncludesTaskDetails()
    {
        var taskId = await CreateTask("Build login page");

        // Assign task to engineer-1
        using (var scope = _serviceProvider.CreateScope())
        {
            var taskQuery = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await taskQuery.AssignTaskAsync(taskId, "engineer-1", "Hephaestus");
        }

        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;

        var tasks = (List<Dictionary<string, object?>>)dict["assignedTasks"]!;
        Assert.Single(tasks);
        Assert.Equal("Build login page", tasks[0]["title"]);
        Assert.Equal(1, (int)dict["assignedTaskCount"]!);
    }

    [Fact]
    public async Task HandoffSummary_DoesNotIncludeOtherAgentsTasks()
    {
        var taskId = await CreateTask("Someone else's task");

        // Assign to a different agent
        using (var scope = _serviceProvider.CreateScope())
        {
            var taskQuery = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await taskQuery.AssignTaskAsync(taskId, "planner-1", "Aristotle");
        }

        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var tasks = (List<Dictionary<string, object?>>)dict["assignedTasks"]!;
        Assert.Empty(tasks);
    }

    [Fact]
    public async Task HandoffSummary_IncludesRecentMemories()
    {
        await SeedMemories("engineer-1", 5);

        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)dict["recentMemories"]!;

        Assert.Equal(5, memories.Count);
        Assert.Equal(5, (int)dict["memoryCount"]!);
        Assert.All(memories, m => Assert.Equal("pattern", m["category"]));
    }

    [Fact]
    public async Task HandoffSummary_MemoriesLimitedTo10()
    {
        await SeedMemories("engineer-1", 15);

        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)dict["recentMemories"]!;
        Assert.Equal(10, memories.Count);
    }

    [Fact]
    public async Task HandoffSummary_ExcludesExpiredMemories()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.AgentMemories.Add(new AgentMemoryEntity
            {
                AgentId = "engineer-1",
                Category = "pattern",
                Key = "expired-memory",
                Value = "This is expired",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                UpdatedAt = DateTime.UtcNow.AddHours(-2),
                ExpiresAt = DateTime.UtcNow.AddHours(-1) // already expired
            });
            db.AgentMemories.Add(new AgentMemoryEntity
            {
                AgentId = "engineer-1",
                Category = "pattern",
                Key = "active-memory",
                Value = "This is active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1) // still valid
            });
            await db.SaveChangesAsync();
        }

        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)dict["recentMemories"]!;
        Assert.Single(memories);
        Assert.Equal("active-memory", memories[0]["key"]);
    }

    [Fact]
    public async Task HandoffSummary_DoesNotMutateMemoryAccessMetadata()
    {
        await SeedMemories("engineer-1", 3);

        // Capture original UpdatedAt timestamps
        List<DateTime?> originalTimestamps;
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            originalTimestamps = await db.AgentMemories
                .Where(m => m.AgentId == "engineer-1")
                .OrderBy(m => m.Key)
                .Select(m => m.UpdatedAt)
                .ToListAsync();
        }

        // Execute handoff summary
        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new());
        await handler.ExecuteAsync(cmd, ctx);

        // Verify timestamps unchanged
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var afterTimestamps = await db.AgentMemories
                .Where(m => m.AgentId == "engineer-1")
                .OrderBy(m => m.Key)
                .Select(m => m.UpdatedAt)
                .ToListAsync();

            Assert.Equal(originalTimestamps, afterTimestamps);
        }
    }

    [Fact]
    public async Task HandoffSummary_IncludesSummaryLine()
    {
        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var summary = (string)dict["summary"]!;
        Assert.Contains("Hephaestus", summary);
        Assert.Contains("SoftwareEngineer", summary);
        Assert.Contains("main", summary);
    }

    [Fact]
    public async Task HandoffSummary_DoesNotIncludeOtherAgentsMemories()
    {
        await SeedMemories("planner-1", 3);

        var handler = new HandoffSummaryHandler();
        var (cmd, ctx) = MakeCommand("HANDOFF_SUMMARY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var memories = (List<Dictionary<string, object?>>)dict["recentMemories"]!;
        Assert.Empty(memories);
    }

    // ════════════════════════════════════════════════════════════════
    // PLATFORM_STATUS
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PlatformStatus_ReturnsAllSections()
    {
        var handler = new PlatformStatusHandler();
        var (cmd, ctx) = MakeCommand("PLATFORM_STATUS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;

        Assert.True(dict.ContainsKey("server"));
        Assert.True(dict.ContainsKey("executor"));
        Assert.True(dict.ContainsKey("agents"));
        Assert.True(dict.ContainsKey("rooms"));
        Assert.True(dict.ContainsKey("tasks"));
        Assert.True(dict.ContainsKey("sprint"));
        Assert.True(dict.ContainsKey("connections"));
        Assert.True(dict.ContainsKey("status"));
        Assert.True(dict.ContainsKey("timestamp"));
    }

    [Fact]
    public async Task PlatformStatus_ServerSection_HasUptime()
    {
        var handler = new PlatformStatusHandler();
        var (cmd, ctx) = MakeCommand("PLATFORM_STATUS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var server = (Dictionary<string, object?>)dict["server"]!;

        Assert.NotNull(server["uptime"]);
        Assert.NotNull(server["startedAt"]);
        Assert.NotNull(server["version"]);
        Assert.NotNull(server["workingSetMB"]);
    }

    [Fact]
    public async Task PlatformStatus_ExecutorSection_ShowsHealthy()
    {
        var handler = new PlatformStatusHandler();
        var (cmd, ctx) = MakeCommand("PLATFORM_STATUS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var executor = (Dictionary<string, object?>)dict["executor"]!;

        Assert.True((bool)executor["operational"]!);
        Assert.False((bool)executor["authFailed"]!);
        Assert.Equal("Closed", executor["circuitBreakerState"]);
    }

    [Fact]
    public async Task PlatformStatus_AgentSection_ShowsConfigured()
    {
        var handler = new PlatformStatusHandler();
        var (cmd, ctx) = MakeCommand("PLATFORM_STATUS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var agents = (Dictionary<string, object?>)dict["agents"]!;

        Assert.Equal(2, (int)agents["configured"]!);
    }

    [Fact]
    public async Task PlatformStatus_WithNoSprint_ReturnsNull()
    {
        var handler = new PlatformStatusHandler();
        var (cmd, ctx) = MakeCommand("PLATFORM_STATUS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Null(dict["sprint"]);
    }

    [Fact]
    public async Task PlatformStatus_TaskCounts_IncludeAllStatuses()
    {
        // Create a few tasks
        await CreateTask("Task 1");
        await CreateTask("Task 2");

        var handler = new PlatformStatusHandler();
        var (cmd, ctx) = MakeCommand("PLATFORM_STATUS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var tasks = (Dictionary<string, object?>)dict["tasks"]!;

        Assert.True(tasks.ContainsKey("total"));
        var total = (int)tasks["total"]!;
        Assert.True(total >= 2);
    }

    [Fact]
    public async Task PlatformStatus_ConnectionSection_IncludesSignalR()
    {
        var handler = new PlatformStatusHandler();
        var (cmd, ctx) = MakeCommand("PLATFORM_STATUS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        var connections = (Dictionary<string, object?>)dict["connections"]!;

        Assert.Equal(0, (int)connections["signalr"]!);
    }

    [Fact]
    public async Task PlatformStatus_HealthyWhenAllSystemsGo()
    {
        var handler = new PlatformStatusHandler();
        var (cmd, ctx) = MakeCommand("PLATFORM_STATUS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("healthy", dict["status"]);
    }

    [Fact]
    public async Task PlatformStatus_DegradedWhenExecutorDown()
    {
        // Create a service container with a degraded executor
        var degradedExecutor = Substitute.For<IAgentExecutor>();
        degradedExecutor.IsFullyOperational.Returns(false);
        degradedExecutor.IsAuthFailed.Returns(true);
        degradedExecutor.CircuitBreakerState.Returns(CircuitState.Open);

        var services = new ServiceCollection();
        services.AddSingleton<IAgentExecutor>(degradedExecutor);
        services.AddSingleton<IAgentCatalog>(_catalog);
        using var sp = services.BuildServiceProvider();

        var cmd = new CommandEnvelope(
            Command: "PLATFORM_STATUS",
            Args: new Dictionary<string, object?>(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: "test",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "engineer-1"
        );
        var ctx = new CommandContext(
            AgentId: "engineer-1",
            AgentName: "Hephaestus",
            AgentRole: "SoftwareEngineer",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: sp
        );

        var handler = new PlatformStatusHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("degraded", dict["status"]);

        var executor = (Dictionary<string, object?>)dict["executor"]!;
        Assert.False((bool)executor["operational"]!);
        Assert.True((bool)executor["authFailed"]!);
    }

    [Fact]
    public async Task PlatformStatus_ReturnsPartialData_WhenServiceMissing()
    {
        // Minimal service provider — no DB, no location service, no sprint service
        var services = new ServiceCollection();
        services.AddSingleton<IAgentCatalog>(_catalog);
        using var sp = services.BuildServiceProvider();

        var cmd = new CommandEnvelope(
            Command: "PLATFORM_STATUS",
            Args: new Dictionary<string, object?>(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: "test",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "engineer-1"
        );
        var ctx = new CommandContext(
            AgentId: "engineer-1",
            AgentName: "Hephaestus",
            AgentRole: "SoftwareEngineer",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: sp
        );

        var handler = new PlatformStatusHandler();
        var result = await handler.ExecuteAsync(cmd, ctx);

        // Should still succeed — partial data is fine
        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;

        // Server section should always work (uses Process info)
        Assert.True(dict.ContainsKey("server"));
        // Agent section should still work (from catalog)
        var agents = (Dictionary<string, object?>)dict["agents"]!;
        Assert.Equal(2, (int)agents["configured"]!);
    }

    // ════════════════════════════════════════════════════════════════
    // Registration
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void KnownCommands_IncludesPhase3B()
    {
        Assert.Contains("HANDOFF_SUMMARY", CommandParser.KnownCommands);
        Assert.Contains("PLATFORM_STATUS", CommandParser.KnownCommands);
    }

    [Fact]
    public async Task CommandDescriptions_IncludesPhase3B()
    {
        // Need handlers registered in DI for ListCommandsHandler to discover them
        var services = new ServiceCollection();
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddSingleton<ICommandHandler, HandoffSummaryHandler>();
        services.AddSingleton<ICommandHandler, PlatformStatusHandler>();
        services.AddSingleton<ICommandHandler, ListCommandsHandler>();
        using var sp = services.BuildServiceProvider();

        var handler = new ListCommandsHandler();
        var cmd = new CommandEnvelope(
            Command: "LIST_COMMANDS",
            Args: new Dictionary<string, object?>(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: "test",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "engineer-1"
        );
        var ctx = new CommandContext(
            AgentId: "engineer-1",
            AgentName: "Test",
            AgentRole: "SoftwareEngineer",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: sp
        );

        var result = await handler.ExecuteAsync(cmd, ctx);
        var dict = (Dictionary<string, object?>)result.Result!;
        var commands = (List<Dictionary<string, object?>>)dict["commands"]!;
        var commandNames = commands.Select(c => (string)c["command"]!).ToList();

        Assert.Contains("HANDOFF_SUMMARY", commandNames);
        Assert.Contains("PLATFORM_STATUS", commandNames);

        // Verify descriptions are not "(no description)"
        var handoffCmd = commands.First(c => (string)c["command"]! == "HANDOFF_SUMMARY");
        Assert.NotEqual("(no description)", (string)handoffCmd["description"]!);
        var platformCmd = commands.First(c => (string)c["command"]! == "PLATFORM_STATUS");
        Assert.NotEqual("(no description)", (string)platformCmd["description"]!);
    }
}
