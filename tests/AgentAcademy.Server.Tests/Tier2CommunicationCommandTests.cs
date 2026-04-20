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
/// Tests for Tier 2 Communication command handlers:
/// MentionTaskOwnerHandler and BroadcastToRoomHandler.
/// </summary>
public sealed class Tier2CommunicationCommandTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;
    private readonly IAgentOrchestrator _orchestratorMock;

    public Tier2CommunicationCommandTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _orchestratorMock = Substitute.For<IAgentOrchestrator>();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: ["coding"], EnabledTools: ["code", "code-write"],
                    AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(
                        ["LIST_*", "MENTION_TASK_OWNER", "BROADCAST_TO_ROOM", "DM"], [])),
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: ["chat"],
                    AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(
                        ["*"], [])),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: ["chat"],
                    AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(
                        ["LIST_*", "MENTION_TASK_OWNER", "BROADCAST_TO_ROOM"], []))
            ]
        );

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
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<IConversationSessionService>(sp => sp.GetRequiredService<ConversationSessionService>());
        services.AddScoped<TaskEvidenceService>();
        services.AddScoped<ITaskEvidenceService>(sp => sp.GetRequiredService<TaskEvidenceService>());
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
        services.AddSingleton(_orchestratorMock);
        services.AddSingleton<IAgentOrchestrator>(_orchestratorMock);

        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        // Create rooms used by tests
        db.Rooms.Add(new RoomEntity
        {
            Id = "main",
            Name = "Main Room",
            Status = "Active",
            CurrentPhase = "Intake",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            WorkspacePath = "/test/workspace"
        });
        db.Rooms.Add(new RoomEntity
        {
            Id = "breakout-1",
            Name = "Breakout Alpha",
            Status = "Active",
            CurrentPhase = "Implementation",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            WorkspacePath = "/test/workspace"
        });
        db.Rooms.Add(new RoomEntity
        {
            Id = "archived-room",
            Name = "Old Room",
            Status = "Archived",
            CurrentPhase = "Implementation",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            WorkspacePath = "/test/workspace"
        });

        // Create workspace so rooms resolve
        db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/test/workspace",
            IsActive = true,
            ProjectName = "test-project",
            CreatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        string commandName,
        Dictionary<string, object?> args,
        string agentId = "reviewer-1",
        string agentName = "Socrates",
        string agentRole = "Reviewer",
        string? roomId = "main")
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
            RoomId: roomId,
            BreakoutRoomId: null,
            Services: scope.ServiceProvider
        );
        return (command, context);
    }

    private async Task<string> CreateAndAssignTask(
        string assigneeId = "engineer-1",
        string assigneeName = "Hephaestus",
        string title = "Implement feature X")
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
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        await taskQueries.AssignTaskAsync(result.Task.Id, assigneeId, assigneeName);
        return result.Task.Id;
    }

    private async Task<string> CreateUnassignedTask()
    {
        using var scope = _serviceProvider.CreateScope();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var result = await taskOrchestration.CreateTaskAsync(new TaskAssignmentRequest(
            Title: "Unassigned task",
            Description: "No one owns this",
            SuccessCriteria: "N/A",
            RoomId: null,
            PreferredRoles: ["SoftwareEngineer"]
        ));
        return result.Task.Id;
    }

    // ─── MENTION_TASK_OWNER Tests ───────────────────────────────

    [Fact]
    public async Task MentionTaskOwner_MissingTaskId_ReturnsValidationError()
    {
        var handler = new MentionTaskOwnerHandler();
        var (cmd, ctx) = MakeCommand("MENTION_TASK_OWNER",
            new Dictionary<string, object?> { ["message"] = "Please check the tests" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskId", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MentionTaskOwner_MissingMessage_ReturnsValidationError()
    {
        var handler = new MentionTaskOwnerHandler();
        var (cmd, ctx) = MakeCommand("MENTION_TASK_OWNER",
            new Dictionary<string, object?> { ["taskId"] = "some-task" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("message", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MentionTaskOwner_TaskNotFound_ReturnsNotFoundError()
    {
        var handler = new MentionTaskOwnerHandler();
        var (cmd, ctx) = MakeCommand("MENTION_TASK_OWNER",
            new Dictionary<string, object?>
            {
                ["taskId"] = "nonexistent-task",
                ["message"] = "Hello"
            });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task MentionTaskOwner_NoAssignee_ReturnsConflictError()
    {
        var taskId = await CreateUnassignedTask();
        var handler = new MentionTaskOwnerHandler();
        var (cmd, ctx) = MakeCommand("MENTION_TASK_OWNER",
            new Dictionary<string, object?>
            {
                ["taskId"] = taskId,
                ["message"] = "Who owns this?"
            });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("no assigned agent", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MentionTaskOwner_SelfMention_ReturnsConflictError()
    {
        var taskId = await CreateAndAssignTask("reviewer-1", "Socrates");
        var handler = new MentionTaskOwnerHandler();
        var (cmd, ctx) = MakeCommand("MENTION_TASK_OWNER",
            new Dictionary<string, object?>
            {
                ["taskId"] = taskId,
                ["message"] = "Talking to myself"
            },
            agentId: "reviewer-1", agentName: "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("yourself", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MentionTaskOwner_Success_SendsDmAndWakesRecipient()
    {
        var taskId = await CreateAndAssignTask("engineer-1", "Hephaestus");
        var handler = new MentionTaskOwnerHandler();
        var (cmd, ctx) = MakeCommand("MENTION_TASK_OWNER",
            new Dictionary<string, object?>
            {
                ["taskId"] = taskId,
                ["message"] = "Tests are failing on this branch"
            },
            agentId: "reviewer-1", agentName: "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal("Hephaestus", result.Result!["recipient"]);
        Assert.Equal("engineer-1", result.Result!["recipientId"]);
        Assert.Equal(taskId, result.Result!["taskId"]);

        // Verify orchestrator was woken
        _orchestratorMock.Received(1).HandleDirectMessage("engineer-1");
    }

    [Fact]
    public async Task MentionTaskOwner_DmContentIncludesTaskContext()
    {
        var taskId = await CreateAndAssignTask("engineer-1", "Hephaestus", "Fix login bug");
        var handler = new MentionTaskOwnerHandler();
        var (cmd, ctx) = MakeCommand("MENTION_TASK_OWNER",
            new Dictionary<string, object?>
            {
                ["taskId"] = taskId,
                ["message"] = "The auth token is expired"
            },
            agentId: "reviewer-1", agentName: "Socrates", agentRole: "Reviewer");

        await handler.ExecuteAsync(cmd, ctx);

        // Verify the DM was stored with task context in the message
        using var scope = _serviceProvider.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var dms = await messages.GetDirectMessagesForAgentAsync("engineer-1", limit: 10, unreadOnly: true);
        Assert.NotEmpty(dms);
        var dm = dms.First();
        Assert.Contains("Fix login bug", dm.Content);
        Assert.Contains("The auth token is expired", dm.Content);
    }

    [Fact]
    public async Task MentionTaskOwner_NullRoomId_FallsBackToMain()
    {
        var taskId = await CreateAndAssignTask("engineer-1", "Hephaestus");
        var handler = new MentionTaskOwnerHandler();
        var (cmd, ctx) = MakeCommand("MENTION_TASK_OWNER",
            new Dictionary<string, object?>
            {
                ["taskId"] = taskId,
                ["message"] = "FYI"
            },
            agentId: "reviewer-1", agentName: "Socrates", agentRole: "Reviewer",
            roomId: null);

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ─── BROADCAST_TO_ROOM Tests ────────────────────────────────

    [Fact]
    public async Task BroadcastToRoom_MissingRoomId_ReturnsValidationError()
    {
        var handler = new BroadcastToRoomHandler();
        var (cmd, ctx) = MakeCommand("BROADCAST_TO_ROOM",
            new Dictionary<string, object?> { ["message"] = "Hello everyone" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("roomId", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BroadcastToRoom_MissingMessage_ReturnsValidationError()
    {
        var handler = new BroadcastToRoomHandler();
        var (cmd, ctx) = MakeCommand("BROADCAST_TO_ROOM",
            new Dictionary<string, object?> { ["roomId"] = "main" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("message", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BroadcastToRoom_RoomNotFound_ReturnsNotFoundError()
    {
        var handler = new BroadcastToRoomHandler();
        var (cmd, ctx) = MakeCommand("BROADCAST_TO_ROOM",
            new Dictionary<string, object?>
            {
                ["roomId"] = "nonexistent-room",
                ["message"] = "Hello"
            });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task BroadcastToRoom_ArchivedRoom_ReturnsConflictError()
    {
        var handler = new BroadcastToRoomHandler();
        var (cmd, ctx) = MakeCommand("BROADCAST_TO_ROOM",
            new Dictionary<string, object?>
            {
                ["roomId"] = "archived-room",
                ["message"] = "Hello"
            });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("archived", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BroadcastToRoom_Success_PostsMessageToRoom()
    {
        var handler = new BroadcastToRoomHandler();
        var (cmd, ctx) = MakeCommand("BROADCAST_TO_ROOM",
            new Dictionary<string, object?>
            {
                ["roomId"] = "breakout-1",
                ["message"] = "Sprint planning starts now"
            },
            agentId: "planner-1", agentName: "Aristotle", agentRole: "Planner");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal("breakout-1", result.Result!["roomId"]);
        Assert.Equal("Breakout Alpha", result.Result!["roomName"]);
    }

    [Fact]
    public async Task BroadcastToRoom_MessageIncludesSenderIdentity()
    {
        var handler = new BroadcastToRoomHandler();
        var (cmd, ctx) = MakeCommand("BROADCAST_TO_ROOM",
            new Dictionary<string, object?>
            {
                ["roomId"] = "breakout-1",
                ["message"] = "Update: new requirement added"
            },
            agentId: "planner-1", agentName: "Aristotle", agentRole: "Planner");

        await handler.ExecuteAsync(cmd, ctx);

        // Verify message in the room includes sender info
        using var scope = _serviceProvider.CreateScope();
        var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
        var (messages, _) = await roomService.GetRoomMessagesAsync("breakout-1");
        var broadcast = messages.FirstOrDefault(m => m.Content.Contains("Update: new requirement added"));
        Assert.NotNull(broadcast);
        Assert.Contains("Aristotle", broadcast!.Content);
        Assert.Contains("Planner", broadcast.Content);
    }

    [Fact]
    public async Task BroadcastToRoom_CrossRoom_SenderNotInTargetRoom()
    {
        // Agent in main room broadcasts to breakout — should succeed
        var handler = new BroadcastToRoomHandler();
        var (cmd, ctx) = MakeCommand("BROADCAST_TO_ROOM",
            new Dictionary<string, object?>
            {
                ["roomId"] = "breakout-1",
                ["message"] = "Cross-room broadcast"
            },
            agentId: "reviewer-1", agentName: "Socrates", agentRole: "Reviewer",
            roomId: "main");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }
}
