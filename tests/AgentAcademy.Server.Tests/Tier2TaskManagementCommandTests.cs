using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
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
/// Tests for Tier 2C task management command handlers:
/// MarkBlockedHandler, ShowDecisionsHandler.
/// </summary>
public sealed class Tier2TaskManagementCommandTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    public Tier2TaskManagementCommandTests()
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
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: ["coding"], EnabledTools: ["code", "code-write"],
                    AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(
                        ["LIST_*", "TASK_STATUS", "MARK_BLOCKED", "SHOW_DECISIONS",
                         "CLAIM_TASK", "UPDATE_TASK", "ADD_TASK_COMMENT"], [])),
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: ["chat"],
                    AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(
                        ["*"], []))
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
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<IConversationSessionService>(sp => sp.GetRequiredService<ConversationSessionService>());
        services.AddScoped<TaskEvidenceService>();
        services.AddScoped<ITaskEvidenceService>(sp => sp.GetRequiredService<TaskEvidenceService>());

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
        string agentRole = "SoftwareEngineer")
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
            Services: scope.ServiceProvider
        );
        return (command, context);
    }

    private async Task<string> CreateTask(string title = "Test Task", string description = "A test task")
    {
        using var scope = _serviceProvider.CreateScope();
        var taskOrchestration = scope.ServiceProvider.GetRequiredService<ITaskOrchestrationService>();
        var result = await taskOrchestration.CreateTaskAsync(new TaskAssignmentRequest(
            Title: title,
            Description: description,
            SuccessCriteria: "Tests pass",
            RoomId: null,
            PreferredRoles: ["SoftwareEngineer"]
        ));
        return result.Task.Id;
    }

    private async Task AssignTask(string taskId, string agentId, string agentName)
    {
        using var scope = _serviceProvider.CreateScope();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        await taskQueries.AssignTaskAsync(taskId, agentId, agentName);
    }

    private async Task SetTaskStatus(string taskId, TaskStatus status)
    {
        using var scope = _serviceProvider.CreateScope();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        await taskQueries.UpdateTaskStatusAsync(taskId, status);
    }

    private async Task AddTypedComment(string taskId, string agentId, string agentName,
        TaskCommentType commentType, string content)
    {
        using var scope = _serviceProvider.CreateScope();
        var command = new CommandEnvelope(
            Command: "ADD_TASK_COMMENT",
            Args: new() { ["taskId"] = taskId, ["content"] = content, ["type"] = commentType.ToString() },
            Status: CommandStatus.Success, Result: null, Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow, ExecutedBy: agentId);
        var context = new CommandContext(
            AgentId: agentId, AgentName: agentName, AgentRole: "SoftwareEngineer",
            RoomId: "main", BreakoutRoomId: null,
            Services: scope.ServiceProvider);
        var handler = new AddTaskCommentHandler();
        var result = await handler.ExecuteAsync(command, context);
        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── MARK_BLOCKED ────────────────────────────────────────────

    [Fact]
    public async Task MarkBlocked_TransitionsActiveTaskToBlocked()
    {
        var taskId = await CreateTask("Build feature");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["taskId"] = taskId, ["reason"] = "Waiting for API spec" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("Active", (string)dict["previousStatus"]!);
        Assert.Equal("Blocked", (string)dict["status"]!);
        Assert.Equal("Waiting for API spec", (string)dict["reason"]!);
    }

    [Fact]
    public async Task MarkBlocked_TransitionsQueuedTaskToBlocked()
    {
        var taskId = await CreateTask("Queued feature");
        await SetTaskStatus(taskId, TaskStatus.Queued);

        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["taskId"] = taskId, ["reason"] = "Dependencies not ready" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("Queued", (string)dict["previousStatus"]!);
        Assert.Equal("Blocked", (string)dict["status"]!);
    }

    [Fact]
    public async Task MarkBlocked_RecordsBlockerComment()
    {
        var taskId = await CreateTask("Blocked feature");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["taskId"] = taskId, ["reason"] = "External dependency unavailable" });
        await handler.ExecuteAsync(cmd, ctx);

        // Verify a Blocker comment was added
        using var scope = _serviceProvider.CreateScope();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
        var comments = await taskQueries.GetTaskCommentsAsync(taskId);
        var blockerComment = comments.FirstOrDefault(c => c.CommentType == TaskCommentType.Blocker);

        Assert.NotNull(blockerComment);
        Assert.Equal("External dependency unavailable", blockerComment.Content);
        Assert.Equal("Hephaestus", blockerComment.AgentName);
    }

    [Fact]
    public async Task MarkBlocked_MissingTaskId_ReturnsError()
    {
        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["reason"] = "Some reason" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskId", result.Error!);
    }

    [Fact]
    public async Task MarkBlocked_MissingReason_ReturnsError()
    {
        var taskId = await CreateTask("No reason task");

        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("reason", result.Error!);
    }

    [Fact]
    public async Task MarkBlocked_TaskNotFound_ReturnsError()
    {
        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["taskId"] = "nonexistent", ["reason"] = "blocked" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task MarkBlocked_AlreadyBlocked_ReturnsConflict()
    {
        var taskId = await CreateTask("Already blocked task");
        await SetTaskStatus(taskId, TaskStatus.Blocked);

        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["taskId"] = taskId, ["reason"] = "blocked again" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("already Blocked", result.Error!);
    }

    [Fact]
    public async Task MarkBlocked_CompletedTask_ReturnsConflict()
    {
        var taskId = await CreateTask("Completed task");
        await SetTaskStatus(taskId, TaskStatus.Completed);

        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["taskId"] = taskId, ["reason"] = "blocked" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("already Completed", result.Error!);
    }

    [Fact]
    public async Task MarkBlocked_CancelledTask_ReturnsConflict()
    {
        var taskId = await CreateTask("Cancelled task");
        await SetTaskStatus(taskId, TaskStatus.Cancelled);

        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["taskId"] = taskId, ["reason"] = "blocked" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
    }

    [Fact]
    public async Task MarkBlocked_ApprovedTask_ReturnsConflict()
    {
        var taskId = await CreateTask("Approved task");
        await SetTaskStatus(taskId, TaskStatus.Approved);

        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["taskId"] = taskId, ["reason"] = "blocked" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("merge workflow", result.Error!);
    }

    [Fact]
    public async Task MarkBlocked_MergingTask_ReturnsConflict()
    {
        var taskId = await CreateTask("Merging task");
        await SetTaskStatus(taskId, TaskStatus.Merging);

        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["taskId"] = taskId, ["reason"] = "blocked" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("merge workflow", result.Error!);
    }

    [Fact]
    public async Task MarkBlocked_AcceptsValueArgAsTaskId()
    {
        var taskId = await CreateTask("Value arg task");

        var handler = new MarkBlockedHandler();
        var (cmd, ctx) = MakeCommand("MARK_BLOCKED",
            new() { ["value"] = taskId, ["reason"] = "test" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── SHOW_DECISIONS ──────────────────────────────────────────

    [Fact]
    public async Task ShowDecisions_ReturnsDecisionComments()
    {
        var taskId = await CreateTask("Decision task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");

        await AddTypedComment(taskId, "engineer-1", "Hephaestus",
            TaskCommentType.Decision, "Use REST over GraphQL for simplicity");
        await AddTypedComment(taskId, "engineer-1", "Hephaestus",
            TaskCommentType.Decision, "Store config in appsettings.json");
        await AddTypedComment(taskId, "engineer-1", "Hephaestus",
            TaskCommentType.Comment, "This is a regular comment — should not appear");

        var handler = new ShowDecisionsHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DECISIONS", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var decisions = (List<Dictionary<string, object?>>)dict["decisions"]!;
        Assert.Equal(2, decisions.Count);
        Assert.Equal(3, (int)dict["totalComments"]!);
    }

    [Fact]
    public async Task ShowDecisions_NoDecisions_ReturnsEmptyList()
    {
        var taskId = await CreateTask("No decisions task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");

        await AddTypedComment(taskId, "engineer-1", "Hephaestus",
            TaskCommentType.Comment, "Just a regular comment");

        var handler = new ShowDecisionsHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DECISIONS", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var decisions = (List<Dictionary<string, object?>>)dict["decisions"]!;
        Assert.Empty(decisions);
        Assert.Contains("No decisions", (string)dict["message"]!);
    }

    [Fact]
    public async Task ShowDecisions_RespectsCountLimit()
    {
        var taskId = await CreateTask("Count limit task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");

        for (int i = 0; i < 5; i++)
        {
            await AddTypedComment(taskId, "engineer-1", "Hephaestus",
                TaskCommentType.Decision, $"Decision #{i + 1}");
        }

        var handler = new ShowDecisionsHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DECISIONS",
            new() { ["taskId"] = taskId, ["count"] = "2" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var decisions = (List<Dictionary<string, object?>>)dict["decisions"]!;
        Assert.Equal(2, decisions.Count);
    }

    [Fact]
    public async Task ShowDecisions_MissingTaskId_ReturnsError()
    {
        var handler = new ShowDecisionsHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DECISIONS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskId", result.Error!);
    }

    [Fact]
    public async Task ShowDecisions_TaskNotFound_ReturnsError()
    {
        var handler = new ShowDecisionsHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DECISIONS", new() { ["taskId"] = "nonexistent" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task ShowDecisions_OrdersNewestFirst()
    {
        var taskId = await CreateTask("Order test task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");

        await AddTypedComment(taskId, "engineer-1", "Hephaestus",
            TaskCommentType.Decision, "First decision");
        await Task.Delay(50); // Ensure timestamp difference
        await AddTypedComment(taskId, "engineer-1", "Hephaestus",
            TaskCommentType.Decision, "Second decision");

        var handler = new ShowDecisionsHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DECISIONS", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var decisions = (List<Dictionary<string, object?>>)dict["decisions"]!;
        Assert.Equal(2, decisions.Count);
        // Newest first
        Assert.Contains("Second decision", (string)decisions[0]["content"]!);
        Assert.Contains("First decision", (string)decisions[1]["content"]!);
    }

    [Fact]
    public async Task ShowDecisions_AcceptsValueArgAsTaskId()
    {
        var taskId = await CreateTask("Value arg decisions task");

        var handler = new ShowDecisionsHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DECISIONS", new() { ["value"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── Handler discovery ───────────────────────────────────────

    [Fact]
    public void AllNewHandlers_HaveCorrectCommandNames()
    {
        Assert.Equal("MARK_BLOCKED", new MarkBlockedHandler().CommandName);
        Assert.Equal("SHOW_DECISIONS", new ShowDecisionsHandler().CommandName);
    }

    [Fact]
    public void ShowDecisions_IsRetrySafe()
    {
        Assert.True(new ShowDecisionsHandler().IsRetrySafe);
    }

    [Fact]
    public void MarkBlocked_IsNotRetrySafe()
    {
        ICommandHandler handler = new MarkBlockedHandler();
        Assert.False(handler.IsRetrySafe);
    }

    [Fact]
    public void KnownCommands_IncludesNewCommands()
    {
        Assert.Contains("MARK_BLOCKED", CommandParser.KnownCommands);
        Assert.Contains("SHOW_DECISIONS", CommandParser.KnownCommands);
    }
}
