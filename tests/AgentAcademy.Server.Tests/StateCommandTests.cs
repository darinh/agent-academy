using Microsoft.Extensions.Logging.Abstractions;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for Phase 1B state management commands:
/// CLAIM_TASK, RELEASE_TASK, UPDATE_TASK, APPROVE_TASK, REQUEST_CHANGES, SHOW_REVIEW_QUEUE.
/// </summary>
public class StateCommandTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    public StateCommandTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "engineer-2", Name: "Athena", Role: "SoftwareEngineer",
                    Summary: "Engineer 2", StartupPrompt: "prompt", Model: null,
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
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<MessageService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<RoomService>();
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(NSubstitute.Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
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

    // ── CLAIM_TASK ──────────────────────────────────────────────

    [Fact]
    public async Task ClaimTask_Success()
    {
        var taskId = await CreateTestTask();
        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand("CLAIM_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(taskId, result.Result!["taskId"]!.ToString());
        Assert.Equal("Hephaestus", result.Result!["assignedTo"]!.ToString());
    }

    [Fact]
    public async Task ClaimTask_AutoActivatesQueuedTask()
    {
        var taskId = await CreateTestTask(status: "Queued");
        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand("CLAIM_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Active", result.Result!["status"]!.ToString());
    }

    [Fact]
    public async Task ClaimTask_IdempotentForSameAgent()
    {
        var taskId = await CreateTestTask();
        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand("CLAIM_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus");

        await handler.ExecuteAsync(cmd, ctx);
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task ClaimTask_ErrorWhenClaimedByOther()
    {
        var taskId = await CreateTestTask();
        var handler = new ClaimTaskHandler();

        var (cmd1, ctx1) = MakeCommand("CLAIM_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus");
        await handler.ExecuteAsync(cmd1, ctx1);

        var (cmd2, ctx2) = MakeCommand("CLAIM_TASK",
            new() { ["taskId"] = taskId }, "engineer-2", "Athena");
        var result = await handler.ExecuteAsync(cmd2, ctx2);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("already claimed", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClaimTask_ErrorWhenTaskNotFound()
    {
        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand("CLAIM_TASK",
            new() { ["taskId"] = "nonexistent" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not found", result.Error!);
    }

    [Fact]
    public async Task ClaimTask_ErrorWhenMissingTaskId()
    {
        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand("CLAIM_TASK", new(), "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("taskId", result.Error!);
    }

    [Fact]
    public async Task ClaimTask_AcceptsValueShorthand()
    {
        var taskId = await CreateTestTask();
        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand("CLAIM_TASK",
            new() { ["value"] = taskId }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── RELEASE_TASK ────────────────────────────────────────────

    [Fact]
    public async Task ReleaseTask_Success()
    {
        var taskId = await CreateTestTask();
        await ClaimTaskForAgent(taskId, "engineer-1", "Hephaestus");

        var handler = new ReleaseTaskHandler();
        var (cmd, ctx) = MakeCommand("RELEASE_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task ReleaseTask_ErrorWhenNotAssigned()
    {
        var taskId = await CreateTestTask();
        await ClaimTaskForAgent(taskId, "engineer-1", "Hephaestus");

        var handler = new ReleaseTaskHandler();
        var (cmd, ctx) = MakeCommand("RELEASE_TASK",
            new() { ["taskId"] = taskId }, "engineer-2", "Athena");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("claimed by", result.Error!);
    }

    [Fact]
    public async Task ReleaseTask_ErrorWhenUnclaimed()
    {
        var taskId = await CreateTestTask();

        var handler = new ReleaseTaskHandler();
        var (cmd, ctx) = MakeCommand("RELEASE_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not currently claimed", result.Error!);
    }

    [Fact]
    public async Task ReleaseTask_ErrorWhenTaskNotFound()
    {
        var handler = new ReleaseTaskHandler();
        var (cmd, ctx) = MakeCommand("RELEASE_TASK",
            new() { ["taskId"] = "nonexistent" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not found", result.Error!);
    }

    // ── UPDATE_TASK ─────────────────────────────────────────────

    [Fact]
    public async Task UpdateTask_StatusChange()
    {
        var taskId = await CreateTestTask();
        var handler = new UpdateTaskHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK",
            new() { ["taskId"] = taskId, ["status"] = "InReview" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("InReview", result.Result!["status"]!.ToString());
    }

    [Fact]
    public async Task UpdateTask_BlockerSetsBlockedStatus()
    {
        var taskId = await CreateTestTask();
        var handler = new UpdateTaskHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK",
            new() { ["taskId"] = taskId, ["blocker"] = "Waiting for API key" },
            "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Blocked", result.Result!["status"]!.ToString());
        Assert.Contains("Blocked", result.Result!["actions"]!.ToString()!);
    }

    [Fact]
    public async Task UpdateTask_NotePosted()
    {
        var taskId = await CreateTestTask();
        var handler = new UpdateTaskHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK",
            new() { ["taskId"] = taskId, ["note"] = "Making good progress" },
            "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Contains("note posted", result.Result!["actions"]!.ToString()!);
    }

    [Fact]
    public async Task UpdateTask_ErrorWhenNoOptionalArgs()
    {
        var taskId = await CreateTestTask();
        var handler = new UpdateTaskHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("At least one", result.Error!);
    }

    [Fact]
    public async Task UpdateTask_ErrorWhenBlockerAndStatusBothProvided()
    {
        var taskId = await CreateTestTask();
        var handler = new UpdateTaskHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK",
            new() { ["taskId"] = taskId, ["blocker"] = "Blocked", ["status"] = "InReview" },
            "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Cannot specify both", result.Error!);
    }

    [Fact]
    public async Task UpdateTask_ErrorWhenInvalidStatus()
    {
        var taskId = await CreateTestTask();
        var handler = new UpdateTaskHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK",
            new() { ["taskId"] = taskId, ["status"] = "Approved" },
            "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Invalid status", result.Error!);
    }

    [Fact]
    public async Task UpdateTask_ErrorWhenMissingTaskId()
    {
        var handler = new UpdateTaskHandler();
        var (cmd, ctx) = MakeCommand("UPDATE_TASK",
            new() { ["status"] = "Active" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("taskId", result.Error!);
    }

    // ── APPROVE_TASK ────────────────────────────────────────────

    [Fact]
    public async Task ApproveTask_DeniedForEngineer()
    {
        var handler = new ApproveTaskHandler();
        var (cmd, ctx) = MakeCommand("APPROVE_TASK",
            new() { ["taskId"] = "t1" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    [Fact]
    public async Task ApproveTask_SuccessFromInReview()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.InReview));
        var handler = new ApproveTaskHandler();
        var (cmd, ctx) = MakeCommand("APPROVE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Approved", result.Result!["status"]!.ToString());
        Assert.Equal("reviewer-1", result.Result!["reviewerAgentId"]!.ToString());
    }

    [Fact]
    public async Task ApproveTask_SuccessFromAwaitingValidation()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.AwaitingValidation));
        var handler = new ApproveTaskHandler();
        var (cmd, ctx) = MakeCommand("APPROVE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("Approved", result.Result!["status"]!.ToString());
    }

    [Fact]
    public async Task ApproveTask_WithFindings()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.InReview));
        var handler = new ApproveTaskHandler();
        var (cmd, ctx) = MakeCommand("APPROVE_TASK",
            new() { ["taskId"] = taskId, ["findings"] = "Looks good, minor nit on naming" },
            "reviewer-1", "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, (int)result.Result!["reviewRounds"]!);
    }

    [Fact]
    public async Task ApproveTask_ErrorWhenNotReviewable()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Active));
        var handler = new ApproveTaskHandler();
        var (cmd, ctx) = MakeCommand("APPROVE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("must be InReview or AwaitingValidation", result.Error!);
    }

    [Fact]
    public async Task ApproveTask_ErrorWhenMissingTaskId()
    {
        var handler = new ApproveTaskHandler();
        var (cmd, ctx) = MakeCommand("APPROVE_TASK", new(), "reviewer-1", "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("taskId", result.Error!);
    }

    // ── REQUEST_CHANGES ─────────────────────────────────────────

    [Fact]
    public async Task RequestChanges_DeniedForEngineer()
    {
        var handler = new RequestChangesHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_CHANGES",
            new() { ["taskId"] = "t1", ["findings"] = "bugs" }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    [Fact]
    public async Task RequestChanges_Success()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.InReview));
        var handler = new RequestChangesHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_CHANGES",
            new() { ["taskId"] = taskId, ["findings"] = "Error handling is missing in the auth flow" },
            "reviewer-1", "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("ChangesRequested", result.Result!["status"]!.ToString());
        Assert.Equal("reviewer-1", result.Result!["reviewerAgentId"]!.ToString());
        Assert.Equal(1, (int)result.Result!["reviewRounds"]!);
    }

    [Fact]
    public async Task RequestChanges_ErrorWhenMissingFindings()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.InReview));
        var handler = new RequestChangesHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_CHANGES",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("findings", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestChanges_ErrorWhenNotReviewable()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Active));
        var handler = new RequestChangesHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_CHANGES",
            new() { ["taskId"] = taskId, ["findings"] = "Some findings" },
            "reviewer-1", "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("must be InReview or AwaitingValidation", result.Error!);
    }

    [Fact]
    public async Task RequestChanges_ErrorWhenMaxReviewRoundsExceeded()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.InReview), reviewRounds: 5);
        var handler = new RequestChangesHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_CHANGES",
            new() { ["taskId"] = taskId, ["findings"] = "More issues" },
            "reviewer-1", "Socrates", agentRole: "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("maximum", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── SHOW_REVIEW_QUEUE ───────────────────────────────────────

    [Fact]
    public async Task ShowReviewQueue_EmptyQueue()
    {
        var handler = new ShowReviewQueueHandler();
        var (cmd, ctx) = MakeCommand("SHOW_REVIEW_QUEUE", new(), "reviewer-1", "Socrates");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(0, (int)result.Result!["count"]!);
    }

    [Fact]
    public async Task ShowReviewQueue_ReturnsOnlyReviewableTasks()
    {
        await CreateTestTask(status: nameof(TaskStatus.InReview), title: "Review me");
        await CreateTestTask(status: nameof(TaskStatus.AwaitingValidation), title: "Validate me");
        await CreateTestTask(status: nameof(TaskStatus.Active), title: "Active task");
        await CreateTestTask(status: nameof(TaskStatus.Completed), title: "Done task");

        var handler = new ShowReviewQueueHandler();
        var (cmd, ctx) = MakeCommand("SHOW_REVIEW_QUEUE", new(), "reviewer-1", "Socrates");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(2, (int)result.Result!["count"]!);
    }

    // ── Pipeline integration ────────────────────────────────────

    [Fact]
    public async Task Pipeline_ProcessesClaimTaskCommand()
    {
        var taskId = await CreateTestTask();

        var handlers = new ICommandHandler[]
        {
            new ClaimTaskHandler()
        };
        var pipeline = new CommandPipeline(
            handlers, Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandPipeline>.Instance);

        var agentText = $"I'll take this task.\n\nCLAIM_TASK: {taskId}";

        var agent = new AgentDefinition(
            "engineer-1", "Hephaestus", "SoftwareEngineer", "Test", "prompt",
            null, [], [], true, null,
            new CommandPermissionSet(["*"], []));

        using var scope = _serviceProvider.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "engineer-1", agentText, "room-1", agent, scope.ServiceProvider);

        Assert.Single(result.Results);
        Assert.Equal(CommandStatus.Success, result.Results[0].Status);
    }

    [Fact]
    public async Task Pipeline_ProcessesMultipleStateCommands()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.InReview));

        var handlers = new ICommandHandler[]
        {
            new ShowReviewQueueHandler(),
            new ApproveTaskHandler()
        };
        var pipeline = new CommandPipeline(
            handlers, Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandPipeline>.Instance);

        var agentText = $"SHOW_REVIEW_QUEUE:\n\nAPPROVE_TASK:\n  taskId: {taskId}\n  findings: Code looks good";

        var agent = new AgentDefinition(
            "reviewer-1", "Socrates", "Reviewer", "Test", "prompt",
            null, [], [], true, null,
            new CommandPermissionSet(["*"], []));

        using var scope = _serviceProvider.CreateScope();
        var result = await pipeline.ProcessResponseAsync(
            "reviewer-1", agentText, "room-1", agent, scope.ServiceProvider);

        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal(CommandStatus.Success, r.Status));
    }

    // ── CANCEL_TASK ────────────────────────────────────────────

    [Fact]
    public async Task CancelTask_Success_PlannerCanCancel()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Active));
        var gitService = new GitService(NullLogger<GitService>.Instance, "/tmp");
        var handler = new CancelTaskHandler(gitService);
        var (cmd, ctx) = MakeCommand("CANCEL_TASK",
            new() { ["taskId"] = taskId, ["reason"] = "No longer needed" },
            "planner-1", "Aristotle");
        ctx = ctx with { AgentRole = "Planner" };

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(taskId, result.Result!["taskId"]!.ToString());
    }

    [Fact]
    public async Task CancelTask_Success_ReviewerCanCancel()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.InReview));
        var gitService = new GitService(NullLogger<GitService>.Instance, "/tmp");
        var handler = new CancelTaskHandler(gitService);
        var (cmd, ctx) = MakeCommand("CANCEL_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates");
        ctx = ctx with { AgentRole = "Reviewer" };

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);

        // Verify task is actually cancelled in DB
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal(nameof(TaskStatus.Cancelled), task!.Status);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task CancelTask_Denied_EngineerCannotCancel()
    {
        var taskId = await CreateTestTask();
        var gitService = new GitService(NullLogger<GitService>.Instance, "/tmp");
        var handler = new CancelTaskHandler(gitService);
        var (cmd, ctx) = MakeCommand("CANCEL_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("Human", result.Error!);
    }

    [Fact]
    public async Task CancelTask_Success_HumanCanCancel()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Active));
        var gitService = new GitService(NullLogger<GitService>.Instance, "/tmp");
        var handler = new CancelTaskHandler(gitService);
        var (cmd, ctx) = MakeCommand("CANCEL_TASK",
            new() { ["taskId"] = taskId, ["reason"] = "Consultant cleanup" },
            "human", "Human");
        ctx = ctx with { AgentRole = "Human" };

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task CancelTask_Error_AlreadyCancelled()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Cancelled));
        var gitService = new GitService(NullLogger<GitService>.Instance, "/tmp");
        var handler = new CancelTaskHandler(gitService);
        var (cmd, ctx) = MakeCommand("CANCEL_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates");
        ctx = ctx with { AgentRole = "Reviewer" };

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("already", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelTask_Error_AlreadyCompleted()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Completed));
        var gitService = new GitService(NullLogger<GitService>.Instance, "/tmp");
        var handler = new CancelTaskHandler(gitService);
        var (cmd, ctx) = MakeCommand("CANCEL_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates");
        ctx = ctx with { AgentRole = "Reviewer" };

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
    }

    [Fact]
    public async Task CancelTask_Error_MissingTaskId()
    {
        var gitService = new GitService(NullLogger<GitService>.Instance, "/tmp");
        var handler = new CancelTaskHandler(gitService);
        var (cmd, ctx) = MakeCommand("CANCEL_TASK", new(), "reviewer-1", "Socrates");
        ctx = ctx with { AgentRole = "Reviewer" };

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("taskId", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private async Task<string> CreateTestTask(
        string status = "Active",
        string title = "Test Task",
        string roomId = "room-1",
        int reviewRounds = 0)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // Ensure the room exists
        var room = await db.Rooms.FindAsync(roomId);
        if (room == null)
        {
            db.Rooms.Add(new Data.Entities.RoomEntity
            {
                Id = roomId,
                Name = "Test Room",
                Status = "Active",
                CurrentPhase = "Implementation",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var taskId = $"task-{Guid.NewGuid():N}"[..20];
        db.Tasks.Add(new Data.Entities.TaskEntity
        {
            Id = taskId,
            Title = title,
            Description = "Test task description",
            SuccessCriteria = "It works",
            Status = status,
            CurrentPhase = "Implementation",
            RoomId = roomId,
            ReviewRounds = reviewRounds,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return taskId;
    }

    private async Task ClaimTaskForAgent(string taskId, string agentId, string agentName)
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        await runtime.ClaimTaskAsync(taskId, agentId, agentName);
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
