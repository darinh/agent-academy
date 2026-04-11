using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
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
    private readonly WorkspaceRuntime _runtime;
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

        _sprintService = new SprintService(_db, new ActivityBroadcaster(), NullLogger<SprintService>.Instance);

        var catalog = new AgentCatalogOptions("main", "Main Room", []);
        var activityBus = new ActivityBroadcaster();
        var activityPublisher = new ActivityPublisher(_db, activityBus);
        var executor = Substitute.For<IAgentExecutor>();
        var sessionService = new ConversationSessionService(
            _db, new SystemSettingsService(_db), executor,
            NullLogger<ConversationSessionService>.Instance);
        var taskQueries = new TaskQueryService(_db, NullLogger<TaskQueryService>.Instance, catalog);
        var taskLifecycle = new TaskLifecycleService(_db, NullLogger<TaskLifecycleService>.Instance, catalog, activityPublisher);

        var agentLocations = new AgentLocationService(_db, catalog, activityPublisher);

        _runtime = new WorkspaceRuntime(
            _db,
            NullLogger<WorkspaceRuntime>.Instance,
            catalog, activityPublisher, sessionService, taskQueries, taskLifecycle,
            new MessageService(_db, NullLogger<MessageService>.Instance, catalog, activityPublisher, sessionService),
            new BreakoutRoomService(_db, NullLogger<BreakoutRoomService>.Instance, catalog, activityPublisher, sessionService, taskQueries, agentLocations),
            new TaskItemService(_db, NullLogger<TaskItemService>.Instance),
            new RoomService(_db, NullLogger<RoomService>.Instance, catalog, activityPublisher, sessionService,
                new MessageService(_db, NullLogger<MessageService>.Instance, catalog, activityPublisher, sessionService)),
            agentLocations);

        _controller = new SprintController(
            _sprintService, _runtime,
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
        await _sprintService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument",
            "{\"title\":\"Test\"}", "agent-1");

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
        await _sprintService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", "{}", "a1");
        await _sprintService.AdvanceStageAsync(sprint.Id);
        await _sprintService.StoreArtifactAsync(
            sprint.Id, "Planning", "SprintPlan", "{}", "a1");

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
        await _sprintService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", "{}", "a1");

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
}
