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

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for task item management commands:
/// CREATE_TASK_ITEM, UPDATE_TASK_ITEM, LIST_TASK_ITEMS.
/// </summary>
public class TaskItemCommandTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public TaskItemCommandTests()
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
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false)
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(catalog);
        services.AddSingleton<IAgentCatalog>(catalog);
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<MessageService>();
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddScoped<AgentLocationService>();
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddScoped<IBreakoutRoomService>(sp => sp.GetRequiredService<BreakoutRoomService>());
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<RoomService>();
        services.AddScoped<RoomSnapshotBuilder>();
        services.AddSingleton<ILogger<WorkspaceRoomService>>(NullLogger<WorkspaceRoomService>.Instance);
        services.AddScoped<WorkspaceRoomService>();
        services.AddSingleton<ILogger<RoomLifecycleService>>(NullLogger<RoomLifecycleService>.Instance);
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<CrashRecoveryService>();
        services.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        services.AddScoped<InitializationService>();
        services.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        services.AddScoped<TaskOrchestrationService>();
        services.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();
        db.Rooms.Add(new RoomEntity
        {
            Id = "room-1",
            Name = "Test Room",
            Status = "Active",
            CurrentPhase = "Implementation",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── CREATE_TASK_ITEM ────────────────────────────────────────

    [Fact]
    public async Task CreateTaskItem_Success_MinimalArgs()
    {
        var handler = new CreateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("CREATE_TASK_ITEM",
            new() { ["title"] = "Implement auth" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Implement auth", result.Result!["title"]!.ToString());
        Assert.Equal("Pending", result.Result!["status"]!.ToString());
        Assert.Equal("engineer-1", result.Result!["assignedTo"]!.ToString());
        Assert.Equal("room-1", result.Result!["roomId"]!.ToString());
        Assert.NotNull(result.Result!["taskItemId"]);
    }

    [Fact]
    public async Task CreateTaskItem_Success_AllArgs()
    {
        var handler = new CreateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("CREATE_TASK_ITEM", new()
        {
            ["title"] = "Write unit tests",
            ["description"] = "Cover edge cases for auth middleware",
            ["assignedTo"] = "reviewer-1",
            ["roomId"] = "room-1"
        }, "planner-1", "Athena", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Write unit tests", result.Result!["title"]!.ToString());
        Assert.Equal("reviewer-1", result.Result!["assignedTo"]!.ToString());
    }

    [Fact]
    public async Task CreateTaskItem_MissingTitle_ReturnsValidationError()
    {
        var handler = new CreateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("CREATE_TASK_ITEM", new(), "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("title", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateTaskItem_EmptyTitle_ReturnsValidationError()
    {
        var handler = new CreateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("CREATE_TASK_ITEM",
            new() { ["title"] = "  " }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task CreateTaskItem_DefaultsAssigneeToCallingAgent()
    {
        var handler = new CreateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("CREATE_TASK_ITEM",
            new() { ["title"] = "My item" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("engineer-1", result.Result!["assignedTo"]!.ToString());
    }

    [Fact]
    public async Task CreateTaskItem_DefaultsRoomToCallerRoom()
    {
        var handler = new CreateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("CREATE_TASK_ITEM",
            new() { ["title"] = "Room default test" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("room-1", result.Result!["roomId"]!.ToString());
    }

    // ── UPDATE_TASK_ITEM ────────────────────────────────────────

    [Fact]
    public async Task UpdateTaskItem_Success_SetActive()
    {
        var itemId = await CreateTestItem("engineer-1");
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM",
            new() { ["taskItemId"] = itemId, ["status"] = "Active" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Active", result.Result!["status"]!.ToString());
    }

    [Fact]
    public async Task UpdateTaskItem_Success_SetDoneWithEvidence()
    {
        var itemId = await CreateTestItem("engineer-1");
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM", new()
        {
            ["taskItemId"] = itemId,
            ["status"] = "Done",
            ["evidence"] = "Tests passing, commit abc123"
        }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Done", result.Result!["status"]!.ToString());
        Assert.Equal("Tests passing, commit abc123", result.Result!["evidence"]!.ToString());
    }

    [Fact]
    public async Task UpdateTaskItem_MissingTaskItemId_ReturnsError()
    {
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM",
            new() { ["status"] = "Done" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskItemId", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateTaskItem_MissingStatus_ReturnsError()
    {
        var itemId = await CreateTestItem("engineer-1");
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM",
            new() { ["taskItemId"] = itemId }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("status", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateTaskItem_InvalidStatus_ReturnsError()
    {
        var itemId = await CreateTestItem("engineer-1");
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM",
            new() { ["taskItemId"] = itemId, ["status"] = "Banana" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("Banana", result.Error!);
    }

    [Fact]
    public async Task UpdateTaskItem_NotFound_ReturnsError()
    {
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM",
            new() { ["taskItemId"] = "nonexistent", ["status"] = "Done" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task UpdateTaskItem_NonAssigneeNonPlanner_ReturnsPermissionError()
    {
        var itemId = await CreateTestItem("planner-1");
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM",
            new() { ["taskItemId"] = itemId, ["status"] = "Done" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    [Fact]
    public async Task UpdateTaskItem_PlannerCanUpdateAnyItem()
    {
        var itemId = await CreateTestItem("engineer-1");
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM",
            new() { ["taskItemId"] = itemId, ["status"] = "Rejected" },
            "planner-1", "Athena", "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Rejected", result.Result!["status"]!.ToString());
    }

    [Fact]
    public async Task UpdateTaskItem_ReviewerCanUpdateAnyItem()
    {
        var itemId = await CreateTestItem("engineer-1");
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM",
            new() { ["taskItemId"] = itemId, ["status"] = "Done" },
            "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task UpdateTaskItem_CaseInsensitiveStatus()
    {
        var itemId = await CreateTestItem("engineer-1");
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM",
            new() { ["taskItemId"] = itemId, ["status"] = "done" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Done", result.Result!["status"]!.ToString());
    }

    // ── LIST_TASK_ITEMS ─────────────────────────────────────────

    [Fact]
    public async Task ListTaskItems_ReturnsAllItems()
    {
        await CreateTestItem("engineer-1", "Item 1");
        await CreateTestItem("engineer-1", "Item 2");
        await CreateTestItem("planner-1", "Item 3");

        var handler = new ListTaskItemsHandler();
        var (cmd, ctx) = MakeCommand("LIST_TASK_ITEMS", new(), "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(3, (int)result.Result!["count"]!);
    }

    [Fact]
    public async Task ListTaskItems_FilterByRoom()
    {
        // Add a second room
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Rooms.Add(new RoomEntity
            {
                Id = "room-2", Name = "Room 2", Status = "Active",
                CurrentPhase = "Implementation",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await CreateTestItem("engineer-1", "Item in room-1", "room-1");
        await CreateTestItem("engineer-1", "Item in room-2", "room-2");

        var handler = new ListTaskItemsHandler();
        var (cmd, ctx) = MakeCommand("LIST_TASK_ITEMS",
            new() { ["roomId"] = "room-1" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, (int)result.Result!["count"]!);
    }

    [Fact]
    public async Task ListTaskItems_FilterByStatus()
    {
        var id1 = await CreateTestItem("engineer-1", "Pending item");
        await CreateTestItem("engineer-1", "Another pending");

        // Mark one as Done
        using (var scope = _serviceProvider.CreateScope())
        {
            var taskItems = scope.ServiceProvider.GetRequiredService<ITaskItemService>();
            await taskItems.UpdateTaskItemStatusAsync(id1, TaskItemStatus.Done);
        }

        var handler = new ListTaskItemsHandler();
        var (cmd, ctx) = MakeCommand("LIST_TASK_ITEMS",
            new() { ["status"] = "Done" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, (int)result.Result!["count"]!);
    }

    [Fact]
    public async Task ListTaskItems_InvalidStatus_ReturnsError()
    {
        var handler = new ListTaskItemsHandler();
        var (cmd, ctx) = MakeCommand("LIST_TASK_ITEMS",
            new() { ["status"] = "Invalid" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task ListTaskItems_EmptyResult()
    {
        var handler = new ListTaskItemsHandler();
        var (cmd, ctx) = MakeCommand("LIST_TASK_ITEMS", new(), "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(0, (int)result.Result!["count"]!);
    }

    [Fact]
    public async Task ListTaskItems_NoFilters_ReturnsAll()
    {
        await CreateTestItem("engineer-1", "A");
        await CreateTestItem("planner-1", "B");

        var handler = new ListTaskItemsHandler();
        var (cmd, ctx) = MakeCommand("LIST_TASK_ITEMS", new(), "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(2, (int)result.Result!["count"]!);
    }

    // ── Edge cases from adversarial review ────────────────────

    [Fact]
    public async Task CreateTaskItem_AssignedToByName_ResolvesToId()
    {
        var handler = new CreateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("CREATE_TASK_ITEM",
            new() { ["title"] = "Resolve by name", ["assignedTo"] = "Athena" },
            "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("planner-1", result.Result!["assignedTo"]!.ToString());
    }

    [Fact]
    public async Task CreateTaskItem_UnknownAssignee_ReturnsError()
    {
        var handler = new CreateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("CREATE_TASK_ITEM",
            new() { ["title"] = "Bad assignee", ["assignedTo"] = "NonExistent" },
            "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
        Assert.Contains("NonExistent", result.Error!);
    }

    [Fact]
    public async Task CreateTaskItem_NonexistentRoom_ReturnsError()
    {
        var handler = new CreateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("CREATE_TASK_ITEM",
            new() { ["title"] = "Bad room", ["roomId"] = "no-such-room" },
            "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
        Assert.Contains("no-such-room", result.Error!);
    }

    [Fact]
    public async Task UpdateTaskItem_NumericStatus_ReturnsError()
    {
        var itemId = await CreateTestItem("engineer-1");
        var handler = new UpdateTaskItemHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK_ITEM",
            new() { ["taskItemId"] = itemId, ["status"] = "999" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task ListTaskItems_NumericStatus_ReturnsError()
    {
        var handler = new ListTaskItemsHandler();
        var (cmd, ctx) = MakeCommand("LIST_TASK_ITEMS",
            new() { ["status"] = "42" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private async Task<string> CreateTestItem(
        string assignedTo, string title = "Test Item", string roomId = "room-1")
    {
        using var scope = _serviceProvider.CreateScope();
        var taskItems = scope.ServiceProvider.GetRequiredService<ITaskItemService>();
        var item = await taskItems.CreateTaskItemAsync(title, "Test description", assignedTo, roomId, null);
        return item.Id;
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        string commandName,
        Dictionary<string, string> args,
        string agentId = "engineer-1",
        string agentName = "Hephaestus",
        string agentRole = "SoftwareEngineer")
    {
        var scope = _serviceProvider.CreateScope();

        var command = new CommandEnvelope(
            Command: commandName,
            Args: args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
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
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider
        );

        return (command, context);
    }
}
