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
/// Tests for sprint command handlers: START_SPRINT, ADVANCE_STAGE, STORE_ARTIFACT, COMPLETE_SPRINT.
/// </summary>
public class SprintCommandHandlerTests : IDisposable
{
    private const string TestWorkspace = "/tmp/test-workspace";
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public SprintCommandHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddScoped<SprintService>();
        services.AddScoped<ISprintService>(sp => sp.GetRequiredService<SprintService>());
        services.AddScoped<SprintStageService>();
        services.AddScoped<ISprintStageService>(sp => sp.GetRequiredService<SprintStageService>());
        services.AddScoped<SprintArtifactService>();
        services.AddScoped<ISprintArtifactService>(sp => sp.GetRequiredService<SprintArtifactService>());
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton(NullLogger<SprintService>.Instance)
            .AddSingleton(typeof(ILogger<SprintService>), sp => NullLogger<SprintService>.Instance);
        services.AddSingleton(typeof(ILogger<SprintStageService>), sp => NullLogger<SprintStageService>.Instance);
        services.AddSingleton(typeof(ILogger<SprintArtifactService>), sp => NullLogger<SprintArtifactService>.Instance);
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<IConversationSessionService>(sp => sp.GetRequiredService<ConversationSessionService>());
        services.AddSingleton(typeof(ILogger<ConversationSessionService>), sp => NullLogger<ConversationSessionService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton(typeof(ILogger<SystemSettingsService>), sp => NullLogger<SystemSettingsService>.Instance);
        services.AddSingleton(Substitute.For<IAgentExecutor>());

        // WorkspaceRuntime and its dependencies
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddSingleton<ILogger<TaskDependencyService>>(NullLogger<TaskDependencyService>.Instance);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddSingleton<ILogger<MessageService>>(NullLogger<MessageService>.Instance);
        services.AddScoped<MessageService>();
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddSingleton<ILogger<BreakoutRoomService>>(NullLogger<BreakoutRoomService>.Instance);
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
        services.AddSingleton(typeof(ILogger<TaskQueryService>), sp => NullLogger<TaskQueryService>.Instance);
        services.AddSingleton<ILogger<TaskLifecycleService>>(NullLogger<TaskLifecycleService>.Instance);
        services.AddSingleton(CreateTestCatalog());
        services.AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<AgentCatalogOptions>());
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton<IMessageBroadcaster>(sp => sp.GetRequiredService<MessageBroadcaster>());
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        // Seed an active workspace
        db.Workspaces.Add(new WorkspaceEntity
        {
            Path = TestWorkspace,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private static AgentCatalogOptions CreateTestCatalog() => new(
        DefaultRoomId: "main",
        DefaultRoomName: "Main Room",
        Agents: new List<AgentDefinition>());

    private CommandContext CreateContext(IServiceProvider services) => new(
        AgentId: "planner-1",
        AgentName: "Aristotle",
        AgentRole: "Planner",
        RoomId: "main",
        BreakoutRoomId: null,
        Services: services);

    private static CommandEnvelope MakeCommand(string commandName, Dictionary<string, object?>? args = null) =>
        new(
            Command: commandName,
            Args: args ?? new Dictionary<string, object?>(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: Guid.NewGuid().ToString(),
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "planner-1");

    // ── START_SPRINT ─────────────────────────────────────────────

    [Fact]
    public async Task StartSprint_CreatesSprintInActiveWorkspace()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new StartSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("START_SPRINT"), CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, dict["number"]);
        Assert.Equal("Intake", dict["stage"]);
    }

    [Fact]
    public async Task StartSprint_FailsWhenSprintAlreadyActive()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new StartSprintHandler();

        await handler.ExecuteAsync(MakeCommand("START_SPRINT"), CreateContext(scope.ServiceProvider));
        var result = await handler.ExecuteAsync(MakeCommand("START_SPRINT"), CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
        Assert.Contains("already has an active sprint", result.Error);
    }

    [Fact]
    public async Task StartSprint_FailsWithNoActiveWorkspace()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var ws = await db.Workspaces.FirstAsync();
        ws.IsActive = false;
        await db.SaveChangesAsync();

        var handler = new StartSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("START_SPRINT"), CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    // ── ADVANCE_STAGE ────────────────────────────────────────────

    [Fact]
    public async Task AdvanceStage_AdvancesFromIntakeToPlanningWithArtifact()
    {
        using var scope = _serviceProvider.CreateScope();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
        var artifactService = scope.ServiceProvider.GetRequiredService<SprintArtifactService>();
        var sprint = await sprintService.CreateSprintAsync(TestWorkspace);
        await artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);

        var handler = new AdvanceStageHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("ADVANCE_STAGE"), CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("Intake", dict["previousStage"]);
        Assert.Equal("Intake", dict["currentStage"]); // still at Intake until approved
        Assert.Equal("Planning", dict["pendingStage"]);
        Assert.Equal(true, dict["awaitingSignOff"]);
    }

    [Fact]
    public async Task AdvanceStage_FailsWithoutRequiredArtifact()
    {
        using var scope = _serviceProvider.CreateScope();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
        await sprintService.CreateSprintAsync(TestWorkspace);

        var handler = new AdvanceStageHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("ADVANCE_STAGE"), CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("RequirementsDocument", result.Error);
    }

    [Fact]
    public async Task AdvanceStage_AcceptsExplicitSprintId()
    {
        using var scope = _serviceProvider.CreateScope();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
        var artifactService = scope.ServiceProvider.GetRequiredService<SprintArtifactService>();
        var sprint = await sprintService.CreateSprintAsync(TestWorkspace);
        await artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);

        var handler = new AdvanceStageHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("ADVANCE_STAGE", new Dictionary<string, object?> { ["sprintId"] = sprint.Id }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        // Sign-off required for Intake, so awaitingSignOff should be true
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(true, dict["awaitingSignOff"]);
    }

    [Fact]
    public async Task AdvanceStage_FailsWithNoActiveSprint()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new AdvanceStageHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("ADVANCE_STAGE"), CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_IncompleteTasks_BlocksAgent()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();

        var sprint = await sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CurrentStage = "Implementation";
        db.Rooms.Add(new RoomEntity { Id = "prereq-room", Name = "Test", Status = "Active",
            WorkspacePath = TestWorkspace, CreatedAt = DateTime.UtcNow });
        db.Tasks.Add(new TaskEntity { Id = "t-1", Title = "Unfinished", Description = "d",
            SuccessCriteria = "s", Status = "Active", RoomId = "prereq-room",
            SprintId = sprint.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var handler = new AdvanceStageHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("ADVANCE_STAGE"), CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Cannot advance from Implementation", result.Error);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_ForceIgnoredForAgents()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();

        var sprint = await sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CurrentStage = "Implementation";
        db.Rooms.Add(new RoomEntity { Id = "prereq-room2", Name = "Test", Status = "Active",
            WorkspacePath = TestWorkspace, CreatedAt = DateTime.UtcNow });
        db.Tasks.Add(new TaskEntity { Id = "t-2", Title = "Unfinished", Description = "d",
            SuccessCriteria = "s", Status = "Active", RoomId = "prereq-room2",
            SprintId = sprint.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var handler = new AdvanceStageHandler();
        // Agent sends force=true, but it should be ignored (AgentRole="Planner", not "Human")
        var result = await handler.ExecuteAsync(
            MakeCommand("ADVANCE_STAGE", new Dictionary<string, object?> { ["force"] = true }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Cannot advance from Implementation", result.Error);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_ForceAllowedForHumans()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();

        var sprint = await sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CurrentStage = "Implementation";
        db.Rooms.Add(new RoomEntity { Id = "prereq-room3", Name = "Test", Status = "Active",
            WorkspacePath = TestWorkspace, CreatedAt = DateTime.UtcNow });
        db.Tasks.Add(new TaskEntity { Id = "t-3", Title = "Unfinished", Description = "d",
            SuccessCriteria = "s", Status = "Active", RoomId = "prereq-room3",
            SprintId = sprint.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var handler = new AdvanceStageHandler();
        var humanContext = new CommandContext(
            AgentId: "human", AgentName: "Human", AgentRole: "Human",
            RoomId: "main", BreakoutRoomId: null, Services: scope.ServiceProvider);

        var result = await handler.ExecuteAsync(
            MakeCommand("ADVANCE_STAGE", new Dictionary<string, object?> { ["force"] = true }),
            humanContext);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(true, dict["forced"]);
    }

    // ── STORE_ARTIFACT ───────────────────────────────────────────

    [Fact]
    public async Task StoreArtifact_StoresForCurrentStage()
    {
        using var scope = _serviceProvider.CreateScope();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
        await sprintService.CreateSprintAsync(TestWorkspace);

        var handler = new StoreArtifactHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("STORE_ARTIFACT", new Dictionary<string, object?>
            {
                ["type"] = "RequirementsDocument",
                ["content"] = TestArtifactContent.RequirementsDocument
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("Intake", dict["stage"]);
        Assert.Equal("RequirementsDocument", dict["type"]);
        Assert.Equal("planner-1", dict["agentId"]);
    }

    [Fact]
    public async Task StoreArtifact_AcceptsExplicitStageAndSprintId()
    {
        using var scope = _serviceProvider.CreateScope();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
        var sprint = await sprintService.CreateSprintAsync(TestWorkspace);

        var handler = new StoreArtifactHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("STORE_ARTIFACT", new Dictionary<string, object?>
            {
                ["sprintId"] = sprint.Id,
                ["stage"] = "Intake",
                ["type"] = "RequirementsDocument",
                ["content"] = TestArtifactContent.RequirementsDocument
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task StoreArtifact_FailsWithMissingType()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new StoreArtifactHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("STORE_ARTIFACT", new Dictionary<string, object?>
            {
                ["content"] = "some content"
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("type", result.Error);
    }

    [Fact]
    public async Task StoreArtifact_FailsWithMissingContent()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new StoreArtifactHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("STORE_ARTIFACT", new Dictionary<string, object?>
            {
                ["type"] = "RequirementsDocument"
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("content", result.Error);
    }

    [Fact]
    public async Task StoreArtifact_FailsWithInvalidStage()
    {
        using var scope = _serviceProvider.CreateScope();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
        var sprint = await sprintService.CreateSprintAsync(TestWorkspace);

        var handler = new StoreArtifactHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("STORE_ARTIFACT", new Dictionary<string, object?>
            {
                ["sprintId"] = sprint.Id,
                ["stage"] = "InvalidStage",
                ["type"] = "RequirementsDocument",
                ["content"] = "doc"
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    // ── COMPLETE_SPRINT ──────────────────────────────────────────

    [Fact]
    public async Task CompleteSprint_CompletesWithForce()
    {
        using var scope = _serviceProvider.CreateScope();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
        await sprintService.CreateSprintAsync(TestWorkspace);

        var handler = new CompleteSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("COMPLETE_SPRINT", new Dictionary<string, object?>
            {
                ["force"] = "true"
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("Completed", dict["status"]);
        Assert.NotNull(dict["completedAt"]);
    }

    [Fact]
    public async Task CompleteSprint_FailsWithoutForceWhenNotAtFinalStage()
    {
        using var scope = _serviceProvider.CreateScope();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
        await sprintService.CreateSprintAsync(TestWorkspace);

        var handler = new CompleteSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("COMPLETE_SPRINT"), CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("FinalSynthesis", result.Error);
    }

    [Fact]
    public async Task CompleteSprint_FailsWithNoActiveSprint()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new CompleteSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("COMPLETE_SPRINT"), CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task CompleteSprint_AcceptsBooleanForceFlag()
    {
        using var scope = _serviceProvider.CreateScope();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
        await sprintService.CreateSprintAsync(TestWorkspace);

        var handler = new CompleteSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("COMPLETE_SPRINT", new Dictionary<string, object?>
            {
                ["force"] = true
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── Integration: Full lifecycle through commands ──────────────

    [Fact]
    public async Task FullLifecycle_StartAdvanceStoreComplete()
    {
        using var scope = _serviceProvider.CreateScope();
        var ctx = CreateContext(scope.ServiceProvider);
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();

        // Start sprint
        var startResult = await new StartSprintHandler().ExecuteAsync(
            MakeCommand("START_SPRINT"), ctx);
        Assert.Equal(CommandStatus.Success, startResult.Status);
        var sprintId = (string)((Dictionary<string, object?>)startResult.Result!)["sprintId"]!;

        // Store artifact for Intake
        var storeResult = await new StoreArtifactHandler().ExecuteAsync(
            MakeCommand("STORE_ARTIFACT", new Dictionary<string, object?>
            {
                ["type"] = "RequirementsDocument",
                ["content"] = TestArtifactContent.RequirementsDocument
            }), ctx);
        Assert.Equal(CommandStatus.Success, storeResult.Status);

        // Advance from Intake — enters AwaitingSignOff
        var advResult = await new AdvanceStageHandler().ExecuteAsync(
            MakeCommand("ADVANCE_STAGE"), ctx);
        Assert.Equal(CommandStatus.Success, advResult.Status);
        var advDict = (Dictionary<string, object?>)advResult.Result!;
        Assert.Equal(true, advDict["awaitingSignOff"]);
        Assert.Equal("Intake", advDict["currentStage"]);

        // Approve the advance
        var stageService = scope.ServiceProvider.GetRequiredService<SprintStageService>();
        await stageService.ApproveAdvanceAsync(sprintId);

        // Force-complete
        var completeResult = await new CompleteSprintHandler().ExecuteAsync(
            MakeCommand("COMPLETE_SPRINT", new Dictionary<string, object?>
            {
                ["force"] = true
            }), ctx);
        Assert.Equal(CommandStatus.Success, completeResult.Status);

        // Verify sprint is completed
        var sprint = await sprintService.GetSprintByIdAsync(sprintId);
        Assert.Equal("Completed", sprint!.Status);
    }

    // ── SCHEDULE_SPRINT ──────────────────────────────────────────

    [Fact]
    public async Task ScheduleSprint_Get_ReturnsNoScheduleWhenNoneExists()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new() { ["action"] = "get" }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(false, dict["hasSchedule"]);
    }

    [Fact]
    public async Task ScheduleSprint_Set_CreatesNewSchedule()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new()
            {
                ["action"] = "set",
                ["cron"] = "0 9 * * 1",
                ["timezone"] = "UTC",
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(true, dict["hasSchedule"]);
        Assert.Equal("0 9 * * 1", dict["cronExpression"]);
        Assert.Equal("UTC", dict["timeZoneId"]);
        Assert.Equal(true, dict["enabled"]);
        Assert.NotNull(dict["scheduleId"]);
    }

    [Fact]
    public async Task ScheduleSprint_Set_UpdatesExistingSchedule()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();

        // Create first
        await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new()
            {
                ["action"] = "set",
                ["cron"] = "0 9 * * 1",
            }),
            CreateContext(scope.ServiceProvider));

        // Update
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new()
            {
                ["action"] = "set",
                ["cron"] = "0 10 * * 2",
                ["enabled"] = "false",
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal("0 10 * * 2", dict["cronExpression"]);
        Assert.Equal(false, dict["enabled"]);
        Assert.Contains("updated", (string)dict["message"]!);
    }

    [Fact]
    public async Task ScheduleSprint_Set_RejectsMissingCron()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new() { ["action"] = "set" }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("cron", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScheduleSprint_Set_RejectsInvalidCron()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new()
            {
                ["action"] = "set",
                ["cron"] = "not-a-cron",
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("Invalid cron", result.Error!);
    }

    [Fact]
    public async Task ScheduleSprint_Set_RejectsInvalidTimezone()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new()
            {
                ["action"] = "set",
                ["cron"] = "0 9 * * 1",
                ["timezone"] = "Mars/Olympus_Mons",
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("Unknown timezone", result.Error!);
    }

    [Fact]
    public async Task ScheduleSprint_Delete_RemovesExistingSchedule()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();

        // Create first
        await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new()
            {
                ["action"] = "set",
                ["cron"] = "0 9 * * 1",
            }),
            CreateContext(scope.ServiceProvider));

        // Delete
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new() { ["action"] = "delete" }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(true, dict["deleted"]);

        // Verify gone
        var getResult = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new() { ["action"] = "get" }),
            CreateContext(scope.ServiceProvider));
        var getDict = Assert.IsType<Dictionary<string, object?>>(getResult.Result);
        Assert.Equal(false, getDict["hasSchedule"]);
    }

    [Fact]
    public async Task ScheduleSprint_Delete_FailsWhenNoSchedule()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new() { ["action"] = "delete" }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task ScheduleSprint_DefaultActionIsGet()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();

        // No action arg — should default to "get"
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT"),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(false, dict["hasSchedule"]);
    }

    [Fact]
    public async Task ScheduleSprint_RejectsUnknownAction()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new() { ["action"] = "purge" }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("purge", result.Error!);
    }

    [Fact]
    public async Task ScheduleSprint_FailsWithNoActiveWorkspace()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var ws = await db.Workspaces.FirstAsync();
        ws.IsActive = false;
        await db.SaveChangesAsync();

        var handler = new ScheduleSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new() { ["action"] = "get" }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("workspace", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScheduleSprint_Get_ReturnsScheduleAfterSet()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();

        await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new()
            {
                ["action"] = "set",
                ["cron"] = "30 14 * * 5",
                ["timezone"] = "America/New_York",
                ["enabled"] = false,
            }),
            CreateContext(scope.ServiceProvider));

        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new() { ["action"] = "get" }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(true, dict["hasSchedule"]);
        Assert.Equal("30 14 * * 5", dict["cronExpression"]);
        Assert.Equal("America/New_York", dict["timeZoneId"]);
        Assert.Equal(false, dict["enabled"]);
        Assert.NotNull(dict["nextRunAtUtc"]);
    }

    [Fact]
    public async Task ScheduleSprint_Set_EnabledBoolArgWorks()
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = new ScheduleSprintHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SCHEDULE_SPRINT", new()
            {
                ["action"] = "set",
                ["cron"] = "0 9 * * 1",
                ["enabled"] = true,
            }),
            CreateContext(scope.ServiceProvider));

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(true, dict["enabled"]);
    }
}
