using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
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
        services.AddSingleton(NullLogger<SprintService>.Instance)
            .AddSingleton(typeof(ILogger<SprintService>), sp => NullLogger<SprintService>.Instance);
        services.AddScoped<ConversationSessionService>();
        services.AddSingleton(typeof(ILogger<ConversationSessionService>), sp => NullLogger<ConversationSessionService>.Instance);
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton(typeof(ILogger<SystemSettingsService>), sp => NullLogger<SystemSettingsService>.Instance);
        services.AddSingleton(Substitute.For<IAgentExecutor>());

        // WorkspaceRuntime and its dependencies
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddSingleton<ILogger<MessageService>>(NullLogger<MessageService>.Instance);
        services.AddScoped<MessageService>();
        services.AddSingleton<ILogger<BreakoutRoomService>>(NullLogger<BreakoutRoomService>.Instance);
        services.AddScoped<AgentLocationService>();
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<RoomService>();
        services.AddSingleton<ILogger<RoomLifecycleService>>(NullLogger<RoomLifecycleService>.Instance);
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<CrashRecoveryService>();
        services.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        services.AddScoped<InitializationService>();
        services.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        services.AddScoped<TaskOrchestrationService>();
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
        services.AddSingleton(typeof(ILogger<TaskQueryService>), sp => NullLogger<TaskQueryService>.Instance);
        services.AddSingleton<ILogger<TaskLifecycleService>>(NullLogger<TaskLifecycleService>.Instance);
        services.AddSingleton(CreateTestCatalog());
        services.AddSingleton<ActivityBroadcaster>();
        services.AddScoped<ActivityPublisher>();

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
        var sprint = await sprintService.CreateSprintAsync(TestWorkspace);
        await sprintService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);

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
        var sprint = await sprintService.CreateSprintAsync(TestWorkspace);
        await sprintService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);

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
        await sprintService.ApproveAdvanceAsync(sprintId);

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
}
