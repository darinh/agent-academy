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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for ADD_TASK_DEPENDENCY and REMOVE_TASK_DEPENDENCY command handlers.
/// These are the agent-facing interfaces that wrap TaskDependencyService.
/// </summary>
public sealed class TaskDependencyHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public TaskDependencyHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "planner-1", Name: "Athena", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true)
            ]);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton(catalog);
        services.AddSingleton<IAgentCatalog>(catalog);
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton<IMessageBroadcaster>(sp => sp.GetRequiredService<MessageBroadcaster>());
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());
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
        services.AddScoped<TaskItemService>();
        services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<RoomService>();
        services.AddScoped<IRoomService>(sp => sp.GetRequiredService<RoomService>());
        services.AddScoped<RoomSnapshotBuilder>();

        services.AddScoped<IRoomSnapshotBuilder>(sp => sp.GetRequiredService<RoomSnapshotBuilder>());
        services.AddScoped<WorkspaceRoomService>();

        services.AddScoped<IWorkspaceRoomService>(sp => sp.GetRequiredService<WorkspaceRoomService>());
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<IRoomLifecycleService>(sp => sp.GetRequiredService<RoomLifecycleService>());
        services.AddScoped<CrashRecoveryService>();
        services.AddScoped<ICrashRecoveryService>(sp => sp.GetRequiredService<CrashRecoveryService>());
        services.AddScoped<InitializationService>();
        services.AddScoped<TaskOrchestrationService>();
        services.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<IConversationSessionService>(sp => sp.GetRequiredService<ConversationSessionService>());
        services.AddSingleton(Substitute.For<IAgentExecutor>());
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        db.Rooms.Add(new RoomEntity
        {
            Id = "room-1", Name = "Test Room", Status = "Active",
            CurrentPhase = "Implementation",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private (CommandEnvelope cmd, CommandContext ctx) MakeCommand(
        string commandName,
        Dictionary<string, object?> args,
        string agentId = "engineer-1",
        string agentName = "Hephaestus",
        string agentRole = "SoftwareEngineer")
    {
        var scope = _serviceProvider.CreateScope();
        var cmd = new CommandEnvelope(
            Command: commandName,
            Args: args,
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: agentId);
        var ctx = new CommandContext(
            AgentId: agentId,
            AgentName: agentName,
            AgentRole: agentRole,
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider);
        return (cmd, ctx);
    }

    private async Task<string> SeedTask(string id, string title, string status = "Active")
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Tasks.Add(new TaskEntity
        {
            Id = id, Title = title, Status = status,
            RoomId = "room-1", AssignedAgentId = "engineer-1",
            SuccessCriteria = "Done when done",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ── ADD_TASK_DEPENDENCY ─────────────────────────────────────

    [Fact]
    public async Task AddDependency_Success_ReturnsTaskInfo()
    {
        await SeedTask("task-a", "Build API");
        await SeedTask("task-b", "Design schema");

        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("task-a", result.Result!["taskId"]!.ToString());
        Assert.Equal("task-b", result.Result!["dependsOnTaskId"]!.ToString());
        Assert.Equal(1, Convert.ToInt32(result.Result!["totalDependencies"]));
    }

    [Fact]
    public async Task AddDependency_Success_UnmetDependencyCount()
    {
        await SeedTask("task-a", "Build API");
        await SeedTask("task-b", "Design schema");

        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, Convert.ToInt32(result.Result!["unmetDependencies"]));
        Assert.Contains("unmet", result.Result!["message"]!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDependency_AllSatisfied_ReturnsZeroUnmet()
    {
        await SeedTask("task-a", "Build API");
        await SeedTask("task-b", "Design schema", "Completed");

        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(0, Convert.ToInt32(result.Result!["unmetDependencies"]));
        Assert.Contains("satisfied", result.Result!["message"]!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDependency_MissingTaskId_ReturnsValidation()
    {
        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskId", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDependency_MissingDependsOnTaskId_ReturnsValidation()
    {
        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("dependsOnTaskId", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDependency_EmptyTaskId_ReturnsValidation()
    {
        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "   ",
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task AddDependency_NonStringTaskId_ReturnsValidation()
    {
        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = 42,
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task AddDependency_SelfDep_ReturnsError()
    {
        await SeedTask("task-a", "Build API");

        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-a"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("itself", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDependency_CancelledTarget_ReturnsError()
    {
        await SeedTask("task-a", "Build API");
        await SeedTask("task-b", "Cancelled work", "Cancelled");

        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("cancelled", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDependency_CompletedSource_ReturnsError()
    {
        await SeedTask("task-a", "Completed task", "Completed");
        await SeedTask("task-b", "Design schema");

        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("completed", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDependency_TaskNotFound_ReturnsError()
    {
        await SeedTask("task-b", "Design schema");

        var handler = new AddTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "nonexistent",
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not found", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDependency_CycleDetected_ReturnsError()
    {
        await SeedTask("task-a", "Build API");
        await SeedTask("task-b", "Design schema");

        var handler = new AddTaskDependencyHandler();

        // A depends on B
        var (cmd1, ctx1) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });
        var r1 = await handler.ExecuteAsync(cmd1, ctx1);
        Assert.Equal(CommandStatus.Success, r1.Status);

        // B depends on A → cycle
        var (cmd2, ctx2) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-b",
            ["dependsOnTaskId"] = "task-a"
        });
        var r2 = await handler.ExecuteAsync(cmd2, ctx2);

        Assert.Equal(CommandStatus.Error, r2.Status);
        Assert.Contains("cycle", r2.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDependency_Duplicate_ReturnsError()
    {
        await SeedTask("task-a", "Build API");
        await SeedTask("task-b", "Design schema");

        var handler = new AddTaskDependencyHandler();

        var (cmd1, ctx1) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });
        await handler.ExecuteAsync(cmd1, ctx1);

        // Same dependency again
        var (cmd2, ctx2) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });
        var result = await handler.ExecuteAsync(cmd2, ctx2);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("already exists", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddDependency_CommandName_IsCorrect()
    {
        var handler = new AddTaskDependencyHandler();
        Assert.Equal("ADD_TASK_DEPENDENCY", handler.CommandName);
    }

    // ── REMOVE_TASK_DEPENDENCY ──────────────────────────────────

    [Fact]
    public async Task RemoveDependency_Success_ReturnsUpdatedInfo()
    {
        await SeedTask("task-a", "Build API");
        await SeedTask("task-b", "Design schema");

        // Add the dependency first
        var addHandler = new AddTaskDependencyHandler();
        var (addCmd, addCtx) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });
        await addHandler.ExecuteAsync(addCmd, addCtx);

        // Now remove it
        var removeHandler = new RemoveTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("REMOVE_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });
        var result = await removeHandler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("task-a", result.Result!["taskId"]!.ToString());
        Assert.Equal("task-b", result.Result!["dependsOnTaskId"]!.ToString());
        Assert.Equal(0, Convert.ToInt32(result.Result!["remainingDependencies"]));
    }

    [Fact]
    public async Task RemoveDependency_MissingTaskId_ReturnsValidation()
    {
        var handler = new RemoveTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("REMOVE_TASK_DEPENDENCY", new()
        {
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskId", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveDependency_MissingDependsOnTaskId_ReturnsValidation()
    {
        var handler = new RemoveTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("REMOVE_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("dependsOnTaskId", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveDependency_NotFound_ReturnsError()
    {
        await SeedTask("task-a", "Build API");
        await SeedTask("task-b", "Design schema");

        var handler = new RemoveTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("REMOVE_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("no dependency", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveDependency_WithMultipleDeps_OnlyRemovesTarget()
    {
        await SeedTask("task-a", "Build API");
        await SeedTask("task-b", "Design schema");
        await SeedTask("task-c", "Set up CI");

        var addHandler = new AddTaskDependencyHandler();

        // A depends on B and C
        var (add1, ctx1) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });
        await addHandler.ExecuteAsync(add1, ctx1);

        var (add2, ctx2) = MakeCommand("ADD_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-c"
        });
        await addHandler.ExecuteAsync(add2, ctx2);

        // Remove only the B dependency
        var removeHandler = new RemoveTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("REMOVE_TASK_DEPENDENCY", new()
        {
            ["taskId"] = "task-a",
            ["dependsOnTaskId"] = "task-b"
        });
        var result = await removeHandler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, Convert.ToInt32(result.Result!["remainingDependencies"]));
    }

    [Fact]
    public async Task RemoveDependency_EmptyArgs_ReturnsValidation()
    {
        var handler = new RemoveTaskDependencyHandler();
        var (cmd, ctx) = MakeCommand("REMOVE_TASK_DEPENDENCY", new());

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public void RemoveDependency_CommandName_IsCorrect()
    {
        var handler = new RemoveTaskDependencyHandler();
        Assert.Equal("REMOVE_TASK_DEPENDENCY", handler.CommandName);
    }
}
