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
/// Tests for Tier 2 task command handlers:
/// TaskStatusHandler, ShowTaskHistoryHandler, ShowDependenciesHandler,
/// RequestReviewHandler, WhoAmIHandler.
/// </summary>
public sealed class Tier2TaskCommandTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    public Tier2TaskCommandTests()
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
                        ["LIST_*", "TASK_STATUS", "SHOW_TASK_HISTORY", "SHOW_DEPENDENCIES",
                         "REQUEST_REVIEW", "WHOAMI", "CLAIM_TASK", "UPDATE_TASK"], [])),
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
                        ["LIST_*", "TASK_STATUS", "SHOW_TASK_HISTORY", "SHOW_DEPENDENCIES",
                         "WHOAMI", "APPROVE_TASK", "REQUEST_CHANGES"], []))
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

    private async Task AddDependency(string taskId, string dependsOnTaskId)
    {
        using var scope = _serviceProvider.CreateScope();
        var depService = scope.ServiceProvider.GetRequiredService<ITaskDependencyService>();
        await depService.AddDependencyAsync(taskId, dependsOnTaskId);
    }

    private async Task AddTaskComment(string taskId, string agentId, string content)
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new AddTaskCommentHandler();
        var (cmd, ctx) = MakeCommand("ADD_TASK_COMMENT",
            new() { ["taskId"] = taskId, ["content"] = content },
            agentId: agentId, agentName: "Tester", agentRole: "SoftwareEngineer");
        // Re-scope since MakeCommand uses its own scope
        var command = new CommandEnvelope(
            Command: "ADD_TASK_COMMENT",
            Args: new() { ["taskId"] = taskId, ["content"] = content },
            Status: CommandStatus.Success, Result: null, Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow, ExecutedBy: agentId);
        var context = new CommandContext(
            AgentId: agentId, AgentName: "Tester", AgentRole: "SoftwareEngineer",
            RoomId: "main", BreakoutRoomId: null,
            Services: scope.ServiceProvider);
        await handler.ExecuteAsync(command, context);
    }

    // ── TASK_STATUS ─────────────────────────────────────────────

    [Fact]
    public async Task TaskStatus_ReturnsFullDetail()
    {
        var taskId = await CreateTask("Implement login", "Build the login form");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        var handler = new TaskStatusHandler();
        var (cmd, ctx) = MakeCommand("TASK_STATUS", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(taskId, (string)dict["id"]!);
        Assert.Equal("Implement login", (string)dict["title"]!);
        Assert.Equal("Active", (string)dict["status"]!);
        Assert.Equal("Hephaestus", (string)dict["assignedTo"]!);
        Assert.NotNull(dict["dependsOn"]);
        Assert.NotNull(dict["evidence"]);
    }

    [Fact]
    public async Task TaskStatus_NotFound_ReturnsError()
    {
        var handler = new TaskStatusHandler();
        var (cmd, ctx) = MakeCommand("TASK_STATUS", new() { ["taskId"] = "nonexistent" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task TaskStatus_MissingArg_ReturnsValidationError()
    {
        var handler = new TaskStatusHandler();
        var (cmd, ctx) = MakeCommand("TASK_STATUS", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task TaskStatus_ValueShorthand_Works()
    {
        var taskId = await CreateTask("Shorthand task");

        var handler = new TaskStatusHandler();
        var (cmd, ctx) = MakeCommand("TASK_STATUS", new() { ["value"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("Shorthand task", (string)dict["title"]!);
    }

    [Fact]
    public async Task TaskStatus_IncludesDependencyInfo()
    {
        var taskA = await CreateTask("Task A");
        var taskB = await CreateTask("Task B");
        await AddDependency(taskB, taskA);

        var handler = new TaskStatusHandler();
        var (cmd, ctx) = MakeCommand("TASK_STATUS", new() { ["taskId"] = taskB });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var deps = (List<Dictionary<string, object?>>)dict["dependsOn"]!;
        Assert.Single(deps);
        Assert.Equal(taskA, (string)deps[0]["taskId"]!);
    }

    // ── SHOW_TASK_HISTORY ───────────────────────────────────────

    [Fact]
    public async Task ShowTaskHistory_ReturnsComments()
    {
        var taskId = await CreateTask("History task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await AddTaskComment(taskId, "engineer-1", "First comment");
        await AddTaskComment(taskId, "engineer-1", "Second comment");

        var handler = new ShowTaskHistoryHandler();
        var (cmd, ctx) = MakeCommand("SHOW_TASK_HISTORY", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(2, (int)dict["totalComments"]!);
        var entries = (List<Dictionary<string, object?>>)dict["entries"]!;
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("comment", (string)e["entryType"]!));
    }

    [Fact]
    public async Task ShowTaskHistory_NotFound_ReturnsError()
    {
        var handler = new ShowTaskHistoryHandler();
        var (cmd, ctx) = MakeCommand("SHOW_TASK_HISTORY", new() { ["taskId"] = "nonexistent" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task ShowTaskHistory_MissingArg_ReturnsValidationError()
    {
        var handler = new ShowTaskHistoryHandler();
        var (cmd, ctx) = MakeCommand("SHOW_TASK_HISTORY", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task ShowTaskHistory_CountLimitsResults()
    {
        var taskId = await CreateTask("Count limit task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        for (var i = 0; i < 5; i++)
            await AddTaskComment(taskId, "engineer-1", $"Comment {i}");

        var handler = new ShowTaskHistoryHandler();
        var (cmd, ctx) = MakeCommand("SHOW_TASK_HISTORY",
            new() { ["taskId"] = taskId, ["count"] = "2" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var entries = (List<Dictionary<string, object?>>)dict["entries"]!;
        Assert.Equal(2, entries.Count);
        Assert.Equal(5, (int)dict["totalComments"]!);
    }

    [Fact]
    public async Task ShowTaskHistory_EmptyHistory_ReturnsZeroEntries()
    {
        var taskId = await CreateTask("Empty history task");

        var handler = new ShowTaskHistoryHandler();
        var (cmd, ctx) = MakeCommand("SHOW_TASK_HISTORY", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(0, (int)dict["count"]!);
    }

    // ── SHOW_DEPENDENCIES ───────────────────────────────────────

    [Fact]
    public async Task ShowDependencies_ReturnsGraph()
    {
        var taskA = await CreateTask("Task A");
        var taskB = await CreateTask("Task B (depends on A)");
        await AddDependency(taskB, taskA);

        var handler = new ShowDependenciesHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DEPENDENCIES", new() { ["taskId"] = taskB });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(1, (int)dict["totalUpstream"]!);
        Assert.Equal(0, (int)dict["totalDownstream"]!);
        Assert.Equal(1, (int)dict["unmetDependencies"]!);
        Assert.True((bool)dict["isBlocked"]!);
    }

    [Fact]
    public async Task ShowDependencies_NoDeps_ReturnsEmpty()
    {
        var taskId = await CreateTask("Standalone task");

        var handler = new ShowDependenciesHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DEPENDENCIES", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(0, (int)dict["totalUpstream"]!);
        Assert.Equal(0, (int)dict["totalDownstream"]!);
        Assert.False((bool)dict["isBlocked"]!);
    }

    [Fact]
    public async Task ShowDependencies_NotFound_ReturnsError()
    {
        var handler = new ShowDependenciesHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DEPENDENCIES", new() { ["taskId"] = "nonexistent" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task ShowDependencies_DownstreamShown()
    {
        var taskA = await CreateTask("Task A");
        var taskB = await CreateTask("Task B (depends on A)");
        await AddDependency(taskB, taskA);

        // Query A — should show B as downstream
        var handler = new ShowDependenciesHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DEPENDENCIES", new() { ["taskId"] = taskA });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(0, (int)dict["totalUpstream"]!);
        Assert.Equal(1, (int)dict["totalDownstream"]!);
    }

    // ── REQUEST_REVIEW ──────────────────────────────────────────

    [Fact]
    public async Task RequestReview_TransitionsToInReview()
    {
        var taskId = await CreateTask("Review me");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW",
            new() { ["taskId"] = taskId, ["summary"] = "Ready for review" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("InReview", (string)dict["status"]!);
        Assert.Equal("Active", (string)dict["previousStatus"]!);
    }

    [Fact]
    public async Task RequestReview_FromChangesRequested_Works()
    {
        var taskId = await CreateTask("Changes requested task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);
        await SetTaskStatus(taskId, TaskStatus.InReview);
        await SetTaskStatus(taskId, TaskStatus.Active); // Back to active
        // Simulate ChangesRequested — need to go through InReview first
        await SetTaskStatus(taskId, TaskStatus.InReview);

        // Use direct DB update for ChangesRequested since the handler won't allow it
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var task = await db.Tasks.FindAsync(taskId);
            if (task != null)
            {
                task.Status = TaskStatus.ChangesRequested.ToString();
                await db.SaveChangesAsync();
            }
        }

        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("InReview", (string)dict["status"]!);
    }

    [Fact]
    public async Task RequestReview_NonReviewableState_ReturnsConflict()
    {
        var taskId = await CreateTask("Completed task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        // Move to a non-reviewable terminal state
        await SetTaskStatus(taskId, TaskStatus.Active);
        await SetTaskStatus(taskId, TaskStatus.InReview);

        // Force to Approved via DB (not reviewable)
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var task = await db.Tasks.FindAsync(taskId);
            if (task != null)
            {
                task.Status = TaskStatus.Approved.ToString();
                await db.SaveChangesAsync();
            }
        }

        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("must be Active", result.Error);
    }

    [Fact]
    public async Task RequestReview_NotAssignee_ReturnsPermissionError()
    {
        var taskId = await CreateTask("Assigned to someone else");
        await AssignTask(taskId, "planner-1", "Aristotle");
        await SetTaskStatus(taskId, TaskStatus.Active);

        // reviewer-1 tries to request review (not assignee, not Planner)
        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW",
            new() { ["taskId"] = taskId },
            agentId: "reviewer-1", agentName: "Socrates", agentRole: "Reviewer");
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    [Fact]
    public async Task RequestReview_PlannerCanRequestForAny()
    {
        var taskId = await CreateTask("Planner review");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW",
            new() { ["taskId"] = taskId },
            agentId: "planner-1", agentName: "Aristotle", agentRole: "Planner");
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task RequestReview_NotFound_ReturnsError()
    {
        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW", new() { ["taskId"] = "nonexistent" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task RequestReview_MissingArg_ReturnsValidationError()
    {
        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    // ── WHOAMI ──────────────────────────────────────────────────

    [Fact]
    public async Task WhoAmI_ReturnsAgentContext()
    {
        var handler = new WhoAmIHandler();
        var (cmd, ctx) = MakeCommand("WHOAMI", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("engineer-1", (string)dict["agentId"]!);
        Assert.Equal("Hephaestus", (string)dict["agentName"]!);
        Assert.Equal("SoftwareEngineer", (string)dict["role"]!);
        Assert.Equal("main", (string)dict["currentRoomId"]!);
    }

    [Fact]
    public async Task WhoAmI_IncludesPermissions()
    {
        var handler = new WhoAmIHandler();
        var (cmd, ctx) = MakeCommand("WHOAMI", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.NotNull(dict["allowedCommands"]);
        Assert.NotNull(dict["enabledTools"]);
    }

    [Fact]
    public async Task WhoAmI_IncludesCapabilityTags()
    {
        var handler = new WhoAmIHandler();
        var (cmd, ctx) = MakeCommand("WHOAMI", new());
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var tags = (List<string>)dict["capabilityTags"]!;
        Assert.Contains("coding", tags);
    }

    [Fact]
    public async Task WhoAmI_PlannerGetsFullPermissions()
    {
        var handler = new WhoAmIHandler();
        var (cmd, ctx) = MakeCommand("WHOAMI", new(),
            agentId: "planner-1", agentName: "Aristotle", agentRole: "Planner");
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("Planner", (string)dict["role"]!);
        var allowed = (List<string>)dict["allowedCommands"]!;
        Assert.Contains("*", allowed);
    }

    // ── Handler discovery ───────────────────────────────────────

    [Fact]
    public void AllNewHandlers_HaveCorrectCommandNames()
    {
        Assert.Equal("TASK_STATUS", new TaskStatusHandler().CommandName);
        Assert.Equal("SHOW_TASK_HISTORY", new ShowTaskHistoryHandler().CommandName);
        Assert.Equal("SHOW_DEPENDENCIES", new ShowDependenciesHandler().CommandName);
        Assert.Equal("REQUEST_REVIEW", new RequestReviewHandler().CommandName);
        Assert.Equal("WHOAMI", new WhoAmIHandler().CommandName);
    }

    [Fact]
    public void ReadOnlyHandlers_AreRetrySafe()
    {
        Assert.True(new TaskStatusHandler().IsRetrySafe);
        Assert.True(new ShowTaskHistoryHandler().IsRetrySafe);
        Assert.True(new ShowDependenciesHandler().IsRetrySafe);
        Assert.True(new WhoAmIHandler().IsRetrySafe);
    }

    [Fact]
    public void RequestReview_IsNotRetrySafe()
    {
        ICommandHandler handler = new RequestReviewHandler();
        Assert.False(handler.IsRetrySafe);
    }
}
