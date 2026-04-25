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
/// Edge-case tests for Tier 2 task command handlers that complement
/// the happy-path coverage in <see cref="Tier2TaskCommandTests"/>.
/// Covers: Human role authorization, AwaitingValidation state,
/// review summary comments, blocked-dependency messaging,
/// unknown-agent degradation, breakout room context, history
/// interleaving with evidence, count clamping, and evidence summaries.
/// </summary>
public sealed class Tier2TaskCommandEdgeCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    public Tier2TaskCommandEdgeCaseTests()
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
                    Permissions: new CommandPermissionSet(["*"], [])),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: ["review"], EnabledTools: ["chat"],
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
        services.AddSingleton<AgentAcademy.Server.Services.AgentWatchdog.IWatchdogAgentRunner>(sp =>
            new TestDoubles.NoOpWatchdogAgentRunner(sp.GetRequiredService<IAgentExecutor>()));
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

    // ── REQUEST_REVIEW edge cases ───────────────────────────────

    [Fact]
    public async Task RequestReview_HumanRoleCanRequestForAnyTask()
    {
        var taskId = await CreateTask("Human-reviewed task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW",
            new() { ["taskId"] = taskId },
            agentId: "human-operator", agentName: "Operator", agentRole: "Human");
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("InReview", (string)dict["status"]!);
    }

    [Fact]
    public async Task RequestReview_FromAwaitingValidation_Works()
    {
        var taskId = await CreateTask("Awaiting validation task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        // Force AwaitingValidation via DB since normal transitions may not reach it directly
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var task = await db.Tasks.FindAsync(taskId);
            task!.Status = TaskStatus.AwaitingValidation.ToString();
            await db.SaveChangesAsync();
        }

        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("InReview", (string)dict["status"]!);
        Assert.Equal("AwaitingValidation", (string)dict["previousStatus"]!);
    }

    [Fact]
    public async Task RequestReview_WithSummary_IncludesPreviousStatus()
    {
        var taskId = await CreateTask("Summary comment task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW",
            new() { ["taskId"] = taskId, ["summary"] = "All tests pass, ready for merge" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("InReview", (string)dict["status"]!);
        Assert.Equal("Active", (string)dict["previousStatus"]!);
        Assert.Contains("submitted for review", (string)dict["message"]!);
    }

    [Fact]
    public async Task RequestReview_WithoutSummary_Succeeds()
    {
        var taskId = await CreateTask("No summary task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("InReview", (string)dict["status"]!);
        Assert.NotNull(dict["reviewRounds"]);
    }

    [Fact]
    public async Task RequestReview_ReviewerRole_Denied()
    {
        var taskId = await CreateTask("Reviewer-denied task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        var handler = new RequestReviewHandler();
        var (cmd, ctx) = MakeCommand("REQUEST_REVIEW",
            new() { ["taskId"] = taskId },
            agentId: "reviewer-1", agentName: "Socrates", agentRole: "Reviewer");
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    // ── SHOW_DEPENDENCIES edge cases ────────────────────────────

    [Fact]
    public async Task ShowDependencies_UnmetDeps_ReportsBlocked()
    {
        var depTask = await CreateTask("Prerequisite (pending)");
        var mainTask = await CreateTask("Main task");
        await AddDependency(mainTask, depTask);

        // depTask stays in Created/Pending → unmet dependency
        var handler = new ShowDependenciesHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DEPENDENCIES", new() { ["taskId"] = mainTask });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.True((bool)dict["isBlocked"]!);
        Assert.Equal(1, (int)dict["unmetDependencies"]!);
        Assert.Contains("blocked", ((string)dict["message"]!).ToLowerInvariant());
    }

    [Fact]
    public async Task ShowDependencies_ValueShorthand_Works()
    {
        var taskId = await CreateTask("Shorthand dep task");

        var handler = new ShowDependenciesHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DEPENDENCIES",
            new() { ["value"] = taskId }); // "value" shorthand, not "taskId"
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(taskId, (string)dict["taskId"]!);
    }

    [Fact]
    public async Task ShowDependencies_MixedMetAndUnmet()
    {
        var satisfiedDep = await CreateTask("Done prerequisite");
        await AssignTask(satisfiedDep, "engineer-1", "Hephaestus");
        await SetTaskStatus(satisfiedDep, TaskStatus.Active);
        await SetTaskStatus(satisfiedDep, TaskStatus.InReview);

        // IsSatisfied checks for Completed status specifically
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var t = await db.Tasks.FindAsync(satisfiedDep);
            t!.Status = TaskStatus.Completed.ToString();
            await db.SaveChangesAsync();
        }

        var unmetDep = await CreateTask("Pending prerequisite");
        var mainTask = await CreateTask("Main with mixed deps");
        await AddDependency(mainTask, satisfiedDep);
        await AddDependency(mainTask, unmetDep);

        var handler = new ShowDependenciesHandler();
        var (cmd, ctx) = MakeCommand("SHOW_DEPENDENCIES", new() { ["taskId"] = mainTask });
        var result = await handler.ExecuteAsync(cmd, ctx);

        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.True((bool)dict["isBlocked"]!);
        Assert.Equal(1, (int)dict["unmetDependencies"]!);
        Assert.Equal(2, (int)dict["totalUpstream"]!);
    }

    // ── WHOAMI edge cases ───────────────────────────────────────

    [Fact]
    public async Task WhoAmI_UnknownAgent_OmitsPermissions()
    {
        var handler = new WhoAmIHandler();
        var (cmd, ctx) = MakeCommand("WHOAMI", new(),
            agentId: "unknown-agent", agentName: "Ghost", agentRole: "Unknown");
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("unknown-agent", (string)dict["agentId"]!);
        Assert.Equal("Ghost", (string)dict["agentName"]!);
        // Agent not in catalog → no permissions/tools fields
        Assert.False(dict.ContainsKey("allowedCommands"));
        Assert.False(dict.ContainsKey("enabledTools"));
    }

    [Fact]
    public async Task WhoAmI_InBreakoutRoom_IncludesBreakoutId()
    {
        var handler = new WhoAmIHandler();
        var scope = _serviceProvider.CreateScope();
        var command = new CommandEnvelope(
            Command: "WHOAMI", Args: new(),
            Status: CommandStatus.Success, Result: null, Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow, ExecutedBy: "engineer-1");
        var context = new CommandContext(
            AgentId: "engineer-1", AgentName: "Hephaestus",
            AgentRole: "SoftwareEngineer",
            RoomId: "main", BreakoutRoomId: "breakout-room-42",
            Services: scope.ServiceProvider,
            WorkingDirectory: "/home/agent/worktree");
        var result = await handler.ExecuteAsync(command, context);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("breakout-room-42", (string)dict["breakoutRoomId"]!);
        Assert.Equal("/home/agent/worktree", (string)dict["workingDirectory"]!);
    }

    // ── SHOW_TASK_HISTORY edge cases ────────────────────────────

    [Fact]
    public async Task ShowTaskHistory_InterleavesCommentsAndEvidence()
    {
        var taskId = await CreateTask("History task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        // Add a comment
        await AddTaskComment(taskId, "engineer-1", "Starting implementation");

        // Add evidence records
        using (var scope = _serviceProvider.CreateScope())
        {
            var evidenceService = scope.ServiceProvider.GetRequiredService<ITaskEvidenceService>();
            await evidenceService.RecordEvidenceAsync(
                taskId, "engineer-1", "Hephaestus",
                EvidencePhase.Baseline, "build", "dotnet", "dotnet build",
                exitCode: 0, outputSnippet: "Build succeeded", passed: true);
            await evidenceService.RecordEvidenceAsync(
                taskId, "engineer-1", "Hephaestus",
                EvidencePhase.After, "test", "dotnet", "dotnet test",
                exitCode: 1, outputSnippet: "1 failure", passed: false);
        }

        // Add another comment after evidence
        await AddTaskComment(taskId, "engineer-1", "Fixing test failure");

        var handler = new ShowTaskHistoryHandler();
        var (cmd, ctx) = MakeCommand("SHOW_TASK_HISTORY", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var entries = (List<Dictionary<string, object?>>)dict["entries"]!;

        Assert.True(entries.Count >= 4, $"Expected >= 4 entries, got {entries.Count}");
        Assert.Equal(2, (int)dict["totalComments"]!);
        Assert.Equal(2, (int)dict["totalEvidence"]!);

        // Verify both entry types are present
        Assert.Contains(entries, e => (string)e["entryType"]! == "comment");
        Assert.Contains(entries, e => (string)e["entryType"]! == "evidence");

        // Verify evidence fields are populated
        var evidenceEntry = entries.First(e => (string)e["entryType"]! == "evidence");
        Assert.NotNull(evidenceEntry["phase"]);
        Assert.NotNull(evidenceEntry["checkName"]);
        Assert.NotNull(evidenceEntry["tool"]);
    }

    [Fact]
    public async Task ShowTaskHistory_CountOverMax_ClampedToFifty()
    {
        var taskId = await CreateTask("Max count task");

        var handler = new ShowTaskHistoryHandler();
        var (cmd, ctx) = MakeCommand("SHOW_TASK_HISTORY",
            new() { ["taskId"] = taskId, ["count"] = "999" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        // Should succeed without error (count clamped to 50)
        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(0, (int)dict["count"]!); // No entries, just verifying no error
    }

    [Fact]
    public async Task ShowTaskHistory_CountAsInt_Works()
    {
        var taskId = await CreateTask("Int count task");

        var handler = new ShowTaskHistoryHandler();
        var (cmd, ctx) = MakeCommand("SHOW_TASK_HISTORY",
            new() { ["taskId"] = taskId, ["count"] = 5 });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task ShowTaskHistory_CountAsLong_Works()
    {
        var taskId = await CreateTask("Long count task");

        var handler = new ShowTaskHistoryHandler();
        var (cmd, ctx) = MakeCommand("SHOW_TASK_HISTORY",
            new() { ["taskId"] = taskId, ["count"] = 3L });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task ShowTaskHistory_NegativeCount_ClampedToOne()
    {
        var taskId = await CreateTask("Negative count task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await AddTaskComment(taskId, "engineer-1", "Comment 1");
        await AddTaskComment(taskId, "engineer-1", "Comment 2");

        var handler = new ShowTaskHistoryHandler();
        var (cmd, ctx) = MakeCommand("SHOW_TASK_HISTORY",
            new() { ["taskId"] = taskId, ["count"] = "-5" });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(1, (int)dict["count"]!); // Clamped to 1
    }

    // ── TASK_STATUS edge cases ──────────────────────────────────

    [Fact]
    public async Task TaskStatus_WithEvidence_ReturnsSummary()
    {
        var taskId = await CreateTask("Evidence task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        using (var scope = _serviceProvider.CreateScope())
        {
            var evidenceService = scope.ServiceProvider.GetRequiredService<ITaskEvidenceService>();
            await evidenceService.RecordEvidenceAsync(
                taskId, "engineer-1", "Hephaestus",
                EvidencePhase.Baseline, "build", "dotnet", "dotnet build",
                exitCode: 0, outputSnippet: "Build succeeded", passed: true);
            await evidenceService.RecordEvidenceAsync(
                taskId, "engineer-1", "Hephaestus",
                EvidencePhase.After, "test", "dotnet", "dotnet test",
                exitCode: 1, outputSnippet: "1 failure", passed: false);
            await evidenceService.RecordEvidenceAsync(
                taskId, "engineer-1", "Hephaestus",
                EvidencePhase.After, "build", "dotnet", "dotnet build",
                exitCode: 0, outputSnippet: "Build succeeded", passed: true);
        }

        var handler = new TaskStatusHandler();
        var (cmd, ctx) = MakeCommand("TASK_STATUS", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var evidence = (Dictionary<string, object?>)dict["evidence"]!;
        Assert.Equal(3, (int)evidence["total"]!);
        Assert.Equal(2, (int)evidence["passed"]!);
        Assert.Equal(1, (int)evidence["failed"]!);

        var phases = (Dictionary<string, object?>)evidence["phases"]!;
        Assert.Equal(1, (int)phases["Baseline"]!);
        Assert.Equal(2, (int)phases["After"]!);
    }

    [Fact]
    public async Task TaskStatus_WithSpecLinks_ReturnsLinks()
    {
        var taskId = await CreateTask("Spec-linked task");
        await AssignTask(taskId, "engineer-1", "Hephaestus");
        await SetTaskStatus(taskId, TaskStatus.Active);

        using (var scope = _serviceProvider.CreateScope())
        {
            var lifecycle = scope.ServiceProvider.GetRequiredService<ITaskLifecycleService>();
            await lifecycle.LinkTaskToSpecAsync(taskId, "007-agent-commands",
                "engineer-1", "Hephaestus", "Implements", "Command handler");
        }

        var handler = new TaskStatusHandler();
        var (cmd, ctx) = MakeCommand("TASK_STATUS", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        var specLinks = (List<string>)dict["specLinks"]!;
        Assert.Single(specLinks);
        Assert.Equal("007-agent-commands", specLinks[0]);
    }

    [Fact]
    public async Task TaskStatus_NullableFieldsPresent()
    {
        var taskId = await CreateTask("Minimal task");

        var handler = new TaskStatusHandler();
        var (cmd, ctx) = MakeCommand("TASK_STATUS", new() { ["taskId"] = taskId });
        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        // Nullable fields should be present but null
        Assert.True(dict.ContainsKey("pullRequestUrl"));
        Assert.True(dict.ContainsKey("pullRequestStatus"));
        Assert.True(dict.ContainsKey("mergeCommitSha"));
        Assert.True(dict.ContainsKey("size"));
    }

    // ── Helpers ─────────────────────────────────────────────────

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
        var handler = new AddTaskCommentHandler();
        await handler.ExecuteAsync(command, context);
    }
}
