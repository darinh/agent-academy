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

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for the evidence ledger: runtime methods (RecordEvidence, QueryEvidence, CheckGates)
/// and the three command handlers (RECORD_EVIDENCE, QUERY_EVIDENCE, CHECK_GATES).
/// </summary>
public class TaskEvidenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public TaskEvidenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false),
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false),
                new AgentDefinition(
                    Id: "architect-1", Name: "Archimedes", Role: "Architect",
                    Summary: "Architect", StartupPrompt: "prompt", Model: null,
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
        services.AddScoped<TaskEvidenceService>();
        services.AddScoped<ITaskEvidenceService>(sp => sp.GetRequiredService<TaskEvidenceService>());
        services.AddScoped<MessageService>();
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

    // ── Runtime: RecordEvidenceAsync ─────────────────────────

    [Fact]
    public async Task RecordEvidence_PersistsAndReturns()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        var evidence = await taskEvidence.RecordEvidenceAsync(
            taskId, "engineer-1", "Hephaestus",
            EvidencePhase.After, "build", "bash",
            "dotnet build", 0, "Build succeeded", true);

        Assert.NotNull(evidence);
        Assert.Equal(taskId, evidence.TaskId);
        Assert.Equal(EvidencePhase.After, evidence.Phase);
        Assert.Equal("build", evidence.CheckName);
        Assert.Equal("bash", evidence.Tool);
        Assert.Equal("dotnet build", evidence.Command);
        Assert.Equal(0, evidence.ExitCode);
        Assert.True(evidence.Passed);
        Assert.Equal("Hephaestus", evidence.AgentName);
    }

    [Fact]
    public async Task RecordEvidence_TruncatesLongOutput()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        var longOutput = new string('x', 1000);
        var evidence = await taskEvidence.RecordEvidenceAsync(
            taskId, "engineer-1", "Hephaestus",
            EvidencePhase.After, "tests", "bash",
            "dotnet test", 0, longOutput, true);

        Assert.NotNull(evidence.OutputSnippet);
        Assert.True(evidence.OutputSnippet.Length <= 500);
    }

    [Fact]
    public async Task RecordEvidence_ThrowsOnMissingTask()
    {
        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            taskEvidence.RecordEvidenceAsync(
                "nonexistent", "engineer-1", "Hephaestus",
                EvidencePhase.After, "build", "bash",
                null, null, null, true));
    }

    // ── Runtime: GetTaskEvidenceAsync ────────────────────────

    [Fact]
    public async Task GetEvidence_ReturnsAllForTask()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
            EvidencePhase.Baseline, "build", "bash", "dotnet build", 0, "OK", true);
        await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
            EvidencePhase.After, "build", "bash", "dotnet build", 0, "OK", true);
        await taskEvidence.RecordEvidenceAsync(taskId, "reviewer-1", "Socrates",
            EvidencePhase.Review, "code-review", "manual", null, null, "LGTM", true);

        var all = await taskQueries.GetTaskEvidenceAsync(taskId);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task GetEvidence_FiltersbyPhase()
    {
        var taskId = await CreateTestTask();

        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
            EvidencePhase.Baseline, "build", "bash", null, 0, "OK", true);
        await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
            EvidencePhase.After, "build", "bash", null, 0, "OK", true);
        await taskEvidence.RecordEvidenceAsync(taskId, "reviewer-1", "Socrates",
            EvidencePhase.Review, "review", "manual", null, null, "OK", true);

        var afterOnly = await taskQueries.GetTaskEvidenceAsync(taskId, EvidencePhase.After);
        Assert.Single(afterOnly);
        Assert.Equal(EvidencePhase.After, afterOnly[0].Phase);
    }

    [Fact]
    public async Task GetEvidence_ThrowsOnMissingTask()
    {
        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            taskQueries.GetTaskEvidenceAsync("nonexistent"));
    }

    // ── Runtime: CheckGatesAsync ─────────────────────────────

    [Fact]
    public async Task CheckGates_ActiveStatus_NotMet()
    {
        var taskId = await CreateTestTask(status: "Active");

        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        var result = await taskEvidence.CheckGatesAsync(taskId);

        Assert.False(result.Met);
        Assert.Equal("Active", result.CurrentPhase);
        Assert.Equal("AwaitingValidation", result.TargetPhase);
        Assert.Equal(1, result.RequiredChecks);
        Assert.Equal(0, result.PassedChecks);
        Assert.Contains("build", result.MissingChecks);
    }

    [Fact]
    public async Task CheckGates_ActiveStatus_MetWithBuildPass()
    {
        var taskId = await CreateTestTask(status: "Active");

        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
            EvidencePhase.After, "build", "bash", "dotnet build", 0, "OK", true);

        var result = await taskEvidence.CheckGatesAsync(taskId);

        Assert.True(result.Met);
        Assert.Equal(1, result.PassedChecks);
    }

    [Fact]
    public async Task CheckGates_AwaitingValidation_Needs2Checks()
    {
        var taskId = await CreateTestTask(status: "AwaitingValidation");

        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        // Only 1 check — not enough
        await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
            EvidencePhase.After, "build", "bash", null, 0, "OK", true);

        var result = await taskEvidence.CheckGatesAsync(taskId);
        Assert.False(result.Met);
        Assert.Equal(2, result.RequiredChecks);
        Assert.Equal(1, result.PassedChecks);

        // Add second check — now met
        await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
            EvidencePhase.After, "tests", "bash", null, 0, "OK", true);

        result = await taskEvidence.CheckGatesAsync(taskId);
        Assert.True(result.Met);
        Assert.Equal(2, result.PassedChecks);
    }

    [Fact]
    public async Task CheckGates_InReview_NeedsReviewPhase()
    {
        var taskId = await CreateTestTask(status: "InReview");

        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        // After-phase evidence doesn't count for InReview→Approved
        await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
            EvidencePhase.After, "build", "bash", null, 0, "OK", true);

        var result = await taskEvidence.CheckGatesAsync(taskId);
        Assert.False(result.Met);

        // Review-phase evidence meets the gate
        await taskEvidence.RecordEvidenceAsync(taskId, "reviewer-1", "Socrates",
            EvidencePhase.Review, "code-review", "manual", null, null, "LGTM", true);

        result = await taskEvidence.CheckGatesAsync(taskId);
        Assert.True(result.Met);
    }

    [Fact]
    public async Task CheckGates_FailedChecksDoNotCount()
    {
        var taskId = await CreateTestTask(status: "Active");

        using var scope = _serviceProvider.CreateScope();
        var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
        var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();

        await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
            EvidencePhase.After, "build", "bash", null, 1, "FAILED", false);

        var result = await taskEvidence.CheckGatesAsync(taskId);
        Assert.False(result.Met);
        Assert.Equal(0, result.PassedChecks);
    }

    // ── RECORD_EVIDENCE Handler ──────────────────────────────

    [Fact]
    public async Task RecordEvidenceHandler_Success()
    {
        var taskId = await CreateTestTask();
        var handler = new RecordEvidenceHandler();
        var (cmd, ctx) = MakeCommand("RECORD_EVIDENCE", new()
        {
            ["taskId"] = taskId,
            ["checkName"] = "build",
            ["passed"] = "true",
            ["phase"] = "After",
            ["tool"] = "bash",
            ["command"] = "dotnet build",
            ["exitCode"] = "0",
            ["output"] = "Build succeeded"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("build", dict["checkName"]);
        Assert.Equal(true, dict["passed"]);
    }

    [Fact]
    public async Task RecordEvidenceHandler_MissingTaskId()
    {
        var handler = new RecordEvidenceHandler();
        var (cmd, ctx) = MakeCommand("RECORD_EVIDENCE", new()
        {
            ["checkName"] = "build",
            ["passed"] = "true"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("taskId", result.Error);
    }

    [Fact]
    public async Task RecordEvidenceHandler_InvalidPhase()
    {
        var taskId = await CreateTestTask();
        var handler = new RecordEvidenceHandler();
        var (cmd, ctx) = MakeCommand("RECORD_EVIDENCE", new()
        {
            ["taskId"] = taskId,
            ["checkName"] = "build",
            ["passed"] = "true",
            ["phase"] = "InvalidPhase"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Invalid phase", result.Error);
    }

    [Fact]
    public async Task RecordEvidenceHandler_PermissionDenied_NonAssignee()
    {
        var taskId = await CreateTestTask();
        var handler = new RecordEvidenceHandler();
        var (cmd, ctx) = MakeCommand("RECORD_EVIDENCE", new()
        {
            ["taskId"] = taskId,
            ["checkName"] = "build",
            ["passed"] = "true"
        }, agentId: "architect-1", agentName: "Archimedes", agentRole: "Architect");

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    [Fact]
    public async Task RecordEvidenceHandler_DefaultPhaseIsAfter()
    {
        var taskId = await CreateTestTask();
        var handler = new RecordEvidenceHandler();
        var (cmd, ctx) = MakeCommand("RECORD_EVIDENCE", new()
        {
            ["taskId"] = taskId,
            ["checkName"] = "lint",
            ["passed"] = "true"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("After", dict["phase"]?.ToString());
    }

    [Fact]
    public async Task RecordEvidenceHandler_HumanCanRecordEvidence()
    {
        var taskId = await CreateTestTask();
        var handler = new RecordEvidenceHandler();
        var (cmd, ctx) = MakeCommand("RECORD_EVIDENCE", new()
        {
            ["taskId"] = taskId,
            ["checkName"] = "manual-verification",
            ["passed"] = "true"
        }, agentId: "human", agentName: "Human", agentRole: "Human");

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── QUERY_EVIDENCE Handler ───────────────────────────────

    [Fact]
    public async Task QueryEvidenceHandler_ReturnsAll()
    {
        var taskId = await CreateTestTask();

        // Record some evidence via runtime
        using (var scope = _serviceProvider.CreateScope())
        {
            var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
            var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
                EvidencePhase.After, "build", "bash", null, 0, "OK", true);
            await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
                EvidencePhase.After, "tests", "bash", null, 0, "OK", true);
        }

        var handler = new QueryEvidenceHandler();
        var (cmd, ctx) = MakeCommand("QUERY_EVIDENCE", new()
        {
            ["taskId"] = taskId
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(2, dict["total"]);
        Assert.Equal(2, dict["passed"]);
        Assert.Equal(0, dict["failed"]);
    }

    [Fact]
    public async Task QueryEvidenceHandler_FiltersByPhase()
    {
        var taskId = await CreateTestTask();

        using (var scope = _serviceProvider.CreateScope())
        {
            var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
            var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
                EvidencePhase.Baseline, "build", "bash", null, 0, "OK", true);
            await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
                EvidencePhase.After, "build", "bash", null, 0, "OK", true);
        }

        var handler = new QueryEvidenceHandler();
        var (cmd, ctx) = MakeCommand("QUERY_EVIDENCE", new()
        {
            ["taskId"] = taskId,
            ["phase"] = "Baseline"
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(1, dict["total"]);
    }

    [Fact]
    public async Task QueryEvidenceHandler_MissingTaskId()
    {
        var handler = new QueryEvidenceHandler();
        var (cmd, ctx) = MakeCommand("QUERY_EVIDENCE", new());

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("taskId", result.Error);
    }

    // ── CHECK_GATES Handler ──────────────────────────────────

    [Fact]
    public async Task CheckGatesHandler_ShowsGateStatus()
    {
        var taskId = await CreateTestTask(status: "Active");

        var handler = new CheckGatesHandler();
        var (cmd, ctx) = MakeCommand("CHECK_GATES", new()
        {
            ["taskId"] = taskId
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(false, dict["met"]);
        Assert.Equal("Active", dict["currentPhase"]);
        Assert.Equal("AwaitingValidation", dict["targetPhase"]);
    }

    [Fact]
    public async Task CheckGatesHandler_MetWhenEvidenceExists()
    {
        var taskId = await CreateTestTask(status: "Active");

        using (var scope = _serviceProvider.CreateScope())
        {
            var taskEvidence = scope.ServiceProvider.GetRequiredService<TaskEvidenceService>();
            var taskQueries = scope.ServiceProvider.GetRequiredService<ITaskQueryService>();
            await taskEvidence.RecordEvidenceAsync(taskId, "engineer-1", "Hephaestus",
                EvidencePhase.After, "build", "bash", null, 0, "OK", true);
        }

        var handler = new CheckGatesHandler();
        var (cmd, ctx) = MakeCommand("CHECK_GATES", new()
        {
            ["taskId"] = taskId
        });

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal(true, dict["met"]);
    }

    [Fact]
    public async Task CheckGatesHandler_MissingTaskId()
    {
        var handler = new CheckGatesHandler();
        var (cmd, ctx) = MakeCommand("CHECK_GATES", new());

        var result = await handler.ExecuteAsync(cmd, ctx);
        Assert.Equal(CommandStatus.Error, result.Status);
    }

    // ── Helpers ──────────────────────────────────────────────

    private async Task<string> CreateTestTask(
        string status = "Active",
        string currentPhase = "Implementation",
        string title = "Test Task",
        string roomId = "room-1")
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var room = await db.Rooms.FindAsync(roomId);
        if (room == null)
        {
            db.Rooms.Add(new RoomEntity
            {
                Id = roomId,
                Name = "Test Room",
                Status = "Active",
                CurrentPhase = currentPhase,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var taskId = $"task-{Guid.NewGuid():N}"[..20];
        db.Tasks.Add(new TaskEntity
        {
            Id = taskId,
            Title = title,
            Description = "Test task",
            SuccessCriteria = "",
            Status = status,
            Type = "Feature",
            CurrentPhase = currentPhase,
            CurrentPlan = "",
            ValidationStatus = "NotStarted",
            ValidationSummary = "",
            ImplementationStatus = "NotStarted",
            ImplementationSummary = "",
            PreferredRoles = "[]",
            RoomId = roomId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            FleetModels = "[]",
            TestsCreated = "[]",
            AssignedAgentId = "engineer-1",
            AssignedAgentName = "Hephaestus"
        });
        await db.SaveChangesAsync();
        return taskId;
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
