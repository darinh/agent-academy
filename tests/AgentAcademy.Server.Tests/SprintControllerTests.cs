using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class SprintControllerTests : IDisposable
{
    private const string TestWorkspace = "/tmp/test-workspace";
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SprintService _sprintService;
    private readonly SprintStageService _sprintStageService;
    private readonly SprintArtifactService _artifactService;
    private readonly SprintMetricsCalculator _metricsCalculator;
    private readonly RoomService _roomService;
    private readonly SprintController _controller;

    public SprintControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _sprintService = new SprintService(_db, new ActivityBroadcaster(), new SystemSettingsService(_db), NullLogger<SprintService>.Instance);
        _sprintStageService = new SprintStageService(_db, new ActivityBroadcaster(), NullLogger<SprintStageService>.Instance);
        _artifactService = new SprintArtifactService(_db, new ActivityBroadcaster(), NullLogger<SprintArtifactService>.Instance);
        _metricsCalculator = new SprintMetricsCalculator(_db);

        var catalog = new AgentCatalogOptions("main", "Main Room", []);
        var activityBus = new ActivityBroadcaster();
        var activityPublisher = new ActivityPublisher(_db, activityBus);
        var executor = Substitute.For<IAgentExecutor>();
        var sessionService = new ConversationSessionService(
            _db, new SystemSettingsService(_db), executor,
            NullLogger<ConversationSessionService>.Instance);
        var taskDeps = new TaskDependencyService(_db, NullLogger<TaskDependencyService>.Instance, activityPublisher);
        var taskQueries = new TaskQueryService(_db, NullLogger<TaskQueryService>.Instance, catalog, taskDeps);
        var taskLifecycle = new TaskLifecycleService(_db, NullLogger<TaskLifecycleService>.Instance, catalog, activityPublisher, taskDeps);

        var agentLocations = new AgentLocationService(_db, catalog, activityPublisher);
        var planService = new PlanService(_db);
        var messageService = new MessageService(_db, NullLogger<MessageService>.Instance, catalog, activityPublisher, sessionService, new MessageBroadcaster());
        var breakouts = new BreakoutRoomService(_db, NullLogger<BreakoutRoomService>.Instance, catalog, activityPublisher, sessionService, taskQueries, agentLocations);
        var crashRecovery = new CrashRecoveryService(_db, NullLogger<CrashRecoveryService>.Instance, breakouts, agentLocations, messageService, activityPublisher);
        var roomService = new RoomService(_db, NullLogger<RoomService>.Instance, activityPublisher, messageService, new RoomSnapshotBuilder(_db, catalog, new PhaseTransitionValidator(_db)), new PhaseTransitionValidator(_db));
        var roomLifecycle = new RoomLifecycleService(_db, NullLogger<RoomLifecycleService>.Instance, catalog, activityPublisher);
        var initializationService = new InitializationService(_db, NullLogger<InitializationService>.Instance, catalog, activityPublisher, crashRecovery, roomService, new WorkspaceRoomService(_db, NullLogger<WorkspaceRoomService>.Instance, catalog, activityPublisher));
        var taskOrchestration = new TaskOrchestrationService(_db, NullLogger<TaskOrchestrationService>.Instance, catalog, activityPublisher, taskLifecycle, taskQueries, roomService, new RoomSnapshotBuilder(_db, catalog, new PhaseTransitionValidator(_db)), roomLifecycle, agentLocations, messageService, breakouts, NSubstitute.Substitute.For<AgentAcademy.Server.Services.Contracts.IWorktreeService>());

        _roomService = roomService;

        var scheduleService = new SprintScheduleService(_db);

        _controller = new SprintController(
            _sprintService, _sprintStageService, _artifactService, _metricsCalculator,
            scheduleService, _roomService, sessionService,
            NullLogger<SprintController>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task ActivateWorkspace()
    {
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = TestWorkspace,
            ProjectName = "test",
            IsActive = true,
        });
        await _db.SaveChangesAsync();
    }

    // ── ListSprints ──────────────────────────────────────────────

    [Fact]
    public async Task ListSprints_NoWorkspace_ReturnsEmptyList()
    {
        var result = await _controller.ListSprints();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintListResponse>(ok.Value);
        Assert.Empty(body.Sprints);
        Assert.Equal(0, body.TotalCount);
    }

    [Fact]
    public async Task ListSprints_WithSprints_ReturnsAll()
    {
        await ActivateWorkspace();
        await _sprintService.CreateSprintAsync(TestWorkspace);
        var s2 = await _sprintService.CreateSprintAsync("/other");

        var result = await _controller.ListSprints();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintListResponse>(ok.Value);
        Assert.Single(body.Sprints);
        Assert.Equal(1, body.Sprints[0].Number);
    }

    // ── GetActiveSprint ──────────────────────────────────────────

    [Fact]
    public async Task GetActiveSprint_NoWorkspace_ReturnsNoContent()
    {
        var result = await _controller.GetActiveSprint();
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task GetActiveSprint_NoActiveSprint_ReturnsNoContent()
    {
        await ActivateWorkspace();

        var result = await _controller.GetActiveSprint();
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task GetActiveSprint_WithActiveSprint_ReturnsDetail()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.GetActiveSprint();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintDetailResponse>(ok.Value);
        Assert.Equal(sprint.Id, body.Sprint.Id);
        Assert.Equal(1, body.Sprint.Number);
        Assert.Equal(SprintStatus.Active, body.Sprint.Status);
        Assert.Equal(SprintStage.Intake, body.Sprint.CurrentStage);
        Assert.Equal(6, body.Stages.Count);
        Assert.Empty(body.Artifacts);
    }

    [Fact]
    public async Task GetActiveSprint_WithArtifacts_IncludesArtifacts()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument",
            TestArtifactContent.RequirementsDocument, "agent-1");

        var result = await _controller.GetActiveSprint();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintDetailResponse>(ok.Value);
        Assert.Single(body.Artifacts);
        Assert.Equal(ArtifactType.RequirementsDocument, body.Artifacts[0].Type);
        Assert.Equal("agent-1", body.Artifacts[0].CreatedByAgentId);
    }

    // ── GetSprint ────────────────────────────────────────────────

    [Fact]
    public async Task GetSprint_NotFound_Returns404()
    {
        var result = await _controller.GetSprint("nonexistent");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetSprint_Found_ReturnsDetail()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.GetSprint(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintDetailResponse>(ok.Value);
        Assert.Equal(sprint.Id, body.Sprint.Id);
    }

    // ── GetArtifacts ─────────────────────────────────────────────

    [Fact]
    public async Task GetArtifacts_NotFound_Returns404()
    {
        var result = await _controller.GetArtifacts("nonexistent");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetArtifacts_FiltersByStage()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument, "a1");
        await _sprintStageService.AdvanceStageAsync(sprint.Id);
        await _artifactService.StoreArtifactAsync(
            sprint.Id, "Planning", "SprintPlan", TestArtifactContent.SprintPlan, "a1");

        // All artifacts
        var allResult = await _controller.GetArtifacts(sprint.Id);
        var allOk = Assert.IsType<OkObjectResult>(allResult);
        var allBody = Assert.IsType<List<SprintArtifact>>(allOk.Value);
        Assert.Equal(2, allBody.Count);

        // Filtered by stage
        var filteredResult = await _controller.GetArtifacts(sprint.Id, stage: "Intake");
        var filteredOk = Assert.IsType<OkObjectResult>(filteredResult);
        var filteredBody = Assert.IsType<List<SprintArtifact>>(filteredOk.Value);
        Assert.Single(filteredBody);
        Assert.Equal(ArtifactType.RequirementsDocument, filteredBody[0].Type);
    }

    // ── StartSprint ─────────────────────────────────────────────

    [Fact]
    public async Task StartSprint_NoWorkspace_ReturnsBadRequest()
    {
        var result = await _controller.StartSprint();
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task StartSprint_Success_ReturnsDetail()
    {
        await ActivateWorkspace();

        var result = await _controller.StartSprint();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintDetailResponse>(ok.Value);
        Assert.Equal(1, body.Sprint.Number);
        Assert.Equal(SprintStatus.Active, body.Sprint.Status);
        Assert.Equal(SprintStage.Intake, body.Sprint.CurrentStage);
    }

    [Fact]
    public async Task StartSprint_AlreadyActive_ReturnsConflict()
    {
        await ActivateWorkspace();
        await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.StartSprint();

        Assert.IsType<ConflictObjectResult>(result);
    }

    // ── AdvanceSprint ───────────────────────────────────────────

    [Fact]
    public async Task AdvanceSprint_Success_ReturnsNextStage()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument, "a1");

        var result = await _controller.AdvanceSprint(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintDetailResponse>(ok.Value);
        // Intake requires sign-off, so stays at Intake
        Assert.Equal(SprintStage.Intake, body.Sprint.CurrentStage);
        Assert.True(body.Sprint.AwaitingSignOff);
        Assert.Equal(SprintStage.Planning, body.Sprint.PendingStage);
    }

    [Fact]
    public async Task AdvanceSprint_MissingArtifact_ReturnsConflict()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.AdvanceSprint(sprint.Id);

        Assert.IsType<ConflictObjectResult>(result);
    }

    // Regression: spec 013 §Stage Advancement requires every stage-advance code
    // path to rotate workspace conversation sessions so each stage gets a clean
    // session boundary. AdvanceStageHandler (agent path) and ApproveAdvance
    // (HTTP approve path) both did this; AdvanceSprint (HTTP advance path) did
    // not, leaving rooms stuck on the previous stage's session. See PR #106
    // spec sync that surfaced this divergence.
    [Fact]
    public async Task AdvanceSprint_RealAdvance_RotatesWorkspaceSessions()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        // Jump straight to Discussion — Discussion → Validation requires neither
        // sign-off nor an artifact gate, so the controller advance is a real
        // stage change (the only path where rotation is supposed to fire).
        sprint.CurrentStage = nameof(SprintStage.Discussion);
        await _db.SaveChangesAsync();

        // Seed a non-archived room in the workspace so RotateWorkspaceSessions
        // has something to act on.
        _db.Rooms.Add(new RoomEntity
        {
            Id = "room-1",
            Name = "Main",
            WorkspacePath = TestWorkspace,
            Status = "Idle",
            CurrentPhase = "Intake",
        });
        await _db.SaveChangesAsync();

        var result = await _controller.AdvanceSprint(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintDetailResponse>(ok.Value);
        Assert.Equal(SprintStage.Validation, body.Sprint.CurrentStage);

        // Evidence of rotation: a fresh conversation session exists for the
        // workspace room tagged with the new sprint stage.
        var rotatedSession = await _db.ConversationSessions
            .FirstOrDefaultAsync(s => s.RoomId == "room-1"
                && s.SprintId == sprint.Id
                && s.SprintStage == nameof(SprintStage.Validation));
        Assert.NotNull(rotatedSession);
    }

    // Sign-off path: AdvanceSprint that enters AwaitingSignOff must NOT rotate
    // — the stage didn't actually change, and ApproveAdvance will rotate when
    // the human approves.
    [Fact]
    public async Task AdvanceSprint_AwaitingSignOff_DoesNotRotateWorkspaceSessions()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument, "a1");

        _db.Rooms.Add(new RoomEntity
        {
            Id = "room-signoff",
            Name = "Main",
            WorkspacePath = TestWorkspace,
            Status = "Idle",
            CurrentPhase = "Intake",
        });
        await _db.SaveChangesAsync();

        var result = await _controller.AdvanceSprint(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintDetailResponse>(ok.Value);
        Assert.True(body.Sprint.AwaitingSignOff);
        Assert.Equal(SprintStage.Intake, body.Sprint.CurrentStage);

        // No rotated session should have been created — the stage didn't change.
        var anySession = await _db.ConversationSessions
            .AnyAsync(s => s.RoomId == "room-signoff" && s.SprintId == sprint.Id);
        Assert.False(anySession,
            "AdvanceSprint must not rotate sessions while sprint is awaiting sign-off; ApproveAdvance handles that.");
    }

    // ── CompleteSprint ──────────────────────────────────────────

    [Fact]
    public async Task CompleteSprint_Force_Succeeds()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.CompleteSprint(sprint.Id, force: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintSnapshot>(ok.Value);
        Assert.Equal(SprintStatus.Completed, body.Status);
    }

    [Fact]
    public async Task CompleteSprint_WrongStage_ReturnsConflict()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.CompleteSprint(sprint.Id);

        Assert.IsType<ConflictObjectResult>(result);
    }

    // ── CancelSprint ────────────────────────────────────────────

    [Fact]
    public async Task CancelSprint_Success()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.CancelSprint(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintSnapshot>(ok.Value);
        Assert.Equal(SprintStatus.Cancelled, body.Status);
    }

    [Fact]
    public async Task CancelSprint_NotActive_ReturnsConflict()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _sprintService.CancelSprintAsync(sprint.Id);

        var result = await _controller.CancelSprint(sprint.Id);

        Assert.IsType<ConflictObjectResult>(result);
    }

    // ── BlockSprint / UnblockSprint ──────────────────────────────

    [Fact]
    public async Task BlockSprint_Success_SetsBlockedFieldsAndStaysActive()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.BlockSprint(sprint.Id, new BlockSprintRequest("Self-eval failed"));

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintSnapshot>(ok.Value);
        Assert.Equal(SprintStatus.Active, body.Status);
        Assert.NotNull(body.BlockedAt);
        Assert.Equal("Self-eval failed", body.BlockReason);
    }

    [Fact]
    public async Task BlockSprint_EmptyReason_ReturnsBadRequest()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var nullBody = await _controller.BlockSprint(sprint.Id, null);
        Assert.IsType<BadRequestObjectResult>(nullBody);

        var emptyReason = await _controller.BlockSprint(sprint.Id, new BlockSprintRequest("   "));
        Assert.IsType<BadRequestObjectResult>(emptyReason);
    }

    [Fact]
    public async Task BlockSprint_OnTerminatedSprint_ReturnsConflict()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _sprintService.CancelSprintAsync(sprint.Id);

        var result = await _controller.BlockSprint(sprint.Id, new BlockSprintRequest("late"));

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task BlockSprint_WrongWorkspace_ReturnsNotFound()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync("/tmp/other-workspace");

        var result = await _controller.BlockSprint(sprint.Id, new BlockSprintRequest("reason"));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UnblockSprint_ClearsFlag()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _sprintService.MarkSprintBlockedAsync(sprint.Id, "Stuck");

        var result = await _controller.UnblockSprint(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintSnapshot>(ok.Value);
        Assert.Null(body.BlockedAt);
        Assert.Null(body.BlockReason);
    }

    [Fact]
    public async Task UnblockSprint_NotBlocked_ReturnsOkUnchanged()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.UnblockSprint(sprint.Id);

        Assert.IsType<OkObjectResult>(result);
    }

    // ── ApproveAdvance ──────────────────────────────────────────

    [Fact]
    public async Task ApproveAdvance_Success_ReturnsNextStage()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument, "a1");
        await _sprintStageService.AdvanceStageAsync(sprint.Id); // enters AwaitingSignOff

        var result = await _controller.ApproveAdvance(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintDetailResponse>(ok.Value);
        Assert.Equal(SprintStage.Planning, body.Sprint.CurrentStage);
        Assert.False(body.Sprint.AwaitingSignOff);
        Assert.Null(body.Sprint.PendingStage);
    }

    [Fact]
    public async Task ApproveAdvance_NotAwaitingSignOff_ReturnsConflict()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.ApproveAdvance(sprint.Id);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task ApproveAdvance_NoWorkspace_ReturnsBadRequest()
    {
        var result = await _controller.ApproveAdvance("nonexistent");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ApproveAdvance_WrongWorkspace_ReturnsNotFound()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync("/other-workspace");

        var result = await _controller.ApproveAdvance(sprint.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    // ── RejectAdvance ───────────────────────────────────────────

    [Fact]
    public async Task RejectAdvance_Success_StaysAtCurrentStage()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument, "a1");
        await _sprintStageService.AdvanceStageAsync(sprint.Id); // enters AwaitingSignOff

        var result = await _controller.RejectAdvance(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintSnapshot>(ok.Value);
        Assert.Equal(SprintStage.Intake, body.CurrentStage);
        Assert.False(body.AwaitingSignOff);
        Assert.Null(body.PendingStage);
    }

    [Fact]
    public async Task RejectAdvance_NotAwaitingSignOff_ReturnsConflict()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.RejectAdvance(sprint.Id);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task RejectAdvance_NoWorkspace_ReturnsBadRequest()
    {
        var result = await _controller.RejectAdvance("nonexistent");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RejectAdvance_WrongWorkspace_ReturnsNotFound()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync("/other-workspace");

        var result = await _controller.RejectAdvance(sprint.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    // ── GetSprintMetrics ────────────────────────────────────────

    [Fact]
    public async Task GetSprintMetrics_Success_ReturnsMetrics()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.GetSprintMetrics(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintMetrics>(ok.Value);
        Assert.Equal(sprint.Id, body.SprintId);
        Assert.Equal(1, body.SprintNumber);
        Assert.Equal(SprintStatus.Active, body.Status);
        Assert.Null(body.DurationSeconds); // still active
    }

    [Fact]
    public async Task GetSprintMetrics_NotFound_Returns404()
    {
        await ActivateWorkspace();

        var result = await _controller.GetSprintMetrics("nonexistent");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetSprintMetrics_NoWorkspace_ReturnsBadRequest()
    {
        var result = await _controller.GetSprintMetrics("any-id");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetSprintMetrics_WrongWorkspace_ReturnsNotFound()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync("/other-workspace");

        var result = await _controller.GetSprintMetrics(sprint.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetSprintMetrics_CompletedSprint_HasDuration()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _sprintService.CompleteSprintAsync(sprint.Id, force: true);

        var result = await _controller.GetSprintMetrics(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintMetrics>(ok.Value);
        Assert.NotNull(body.DurationSeconds);
        Assert.Equal(SprintStatus.Completed, body.Status);
    }

    [Fact]
    public async Task GetSprintMetrics_WithArtifacts_CountsThem()
    {
        await ActivateWorkspace();
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument, "a1");
        await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "OverflowRequirements", TestArtifactContent.OverflowRequirements, "a1");

        var result = await _controller.GetSprintMetrics(sprint.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintMetrics>(ok.Value);
        Assert.Equal(2, body.ArtifactCount);
        Assert.Equal(0, body.TaskCount);
        Assert.Equal(0, body.CompletedTaskCount);
    }

    // ── GetMetricsSummary ───────────────────────────────────────

    [Fact]
    public async Task GetMetricsSummary_NoWorkspace_ReturnsEmptySummary()
    {
        var result = await _controller.GetMetricsSummary();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintMetricsSummary>(ok.Value);
        Assert.Equal(0, body.TotalSprints);
        Assert.Equal(0, body.CompletedSprints);
        Assert.Equal(0, body.CancelledSprints);
        Assert.Equal(0, body.ActiveSprints);
        Assert.Null(body.AverageDurationSeconds);
        Assert.Equal(0, body.AverageTaskCount);
        Assert.Equal(0, body.AverageArtifactCount);
        Assert.Empty(body.AverageTimePerStageSeconds);
    }

    [Fact]
    public async Task GetMetricsSummary_NoSprints_ReturnsZeros()
    {
        await ActivateWorkspace();

        var result = await _controller.GetMetricsSummary();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintMetricsSummary>(ok.Value);
        Assert.Equal(0, body.TotalSprints);
        Assert.Equal(0, body.CompletedSprints);
        Assert.Equal(0, body.ActiveSprints);
        Assert.Null(body.AverageDurationSeconds);
    }

    [Fact]
    public async Task GetMetricsSummary_WithSprints_ReturnsCounts()
    {
        await ActivateWorkspace();
        var s1 = await _sprintService.CreateSprintAsync(TestWorkspace);
        await _sprintService.CompleteSprintAsync(s1.Id, force: true);
        var s2 = await _sprintService.CreateSprintAsync(TestWorkspace);

        var result = await _controller.GetMetricsSummary();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintMetricsSummary>(ok.Value);
        Assert.Equal(2, body.TotalSprints);
        Assert.Equal(1, body.CompletedSprints);
        Assert.Equal(1, body.ActiveSprints);
        Assert.NotNull(body.AverageDurationSeconds);
    }

    [Fact]
    public async Task GetMetricsSummary_ScopedToActiveWorkspace()
    {
        await ActivateWorkspace();
        await _sprintService.CreateSprintAsync(TestWorkspace);
        await _sprintService.CreateSprintAsync("/other-workspace");

        var result = await _controller.GetMetricsSummary();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SprintMetricsSummary>(ok.Value);
        Assert.Equal(1, body.TotalSprints);
    }
}
