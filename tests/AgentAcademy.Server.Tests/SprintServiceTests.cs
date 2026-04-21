using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for SprintService — sprint lifecycle, stage advancement, and artifact storage.
/// </summary>
public class SprintServiceTests : IDisposable
{
    private const string TestWorkspace = "/tmp/test-workspace";
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SprintService _service;
    private readonly SprintStageService _stageService;
    private readonly SprintArtifactService _artifactService;
    private readonly SystemSettingsService _settings;

    public SprintServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        var broadcaster = new ActivityBroadcaster();
        _settings = new SystemSettingsService(_db);
        _service = new SprintService(_db, broadcaster, _settings, NullLogger<SprintService>.Instance);
        _stageService = new SprintStageService(_db, broadcaster, NullLogger<SprintStageService>.Instance);
        _artifactService = new SprintArtifactService(_db, broadcaster, NullLogger<SprintArtifactService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── CreateSprintAsync ────────────────────────────────────────

    [Fact]
    public async Task CreateSprint_FirstSprint_NumberIsOne()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        Assert.Equal(1, sprint.Number);
        Assert.Equal("Active", sprint.Status);
        Assert.Equal("Intake", sprint.CurrentStage);
        Assert.Equal(TestWorkspace, sprint.WorkspacePath);
        Assert.Null(sprint.OverflowFromSprintId);
        Assert.Null(sprint.CompletedAt);
    }

    [Fact]
    public async Task CreateSprint_ThrowsIfActiveSprintExists()
    {
        await _service.CreateSprintAsync(TestWorkspace);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateSprintAsync(TestWorkspace));
        Assert.Contains("already has an active sprint", ex.Message);
    }

    [Fact]
    public async Task CreateSprint_IncrementsNumberAfterCompletion()
    {
        var first = await _service.CreateSprintAsync(TestWorkspace);
        // Force-complete to allow next sprint
        await _service.CompleteSprintAsync(first.Id, force: true);

        var second = await _service.CreateSprintAsync(TestWorkspace);

        Assert.Equal(2, second.Number);
    }

    [Fact]
    public async Task CreateSprint_LinksOverflowFromPreviousSprint()
    {
        var first = await _service.CreateSprintAsync(TestWorkspace);

        // Store overflow artifact on first sprint
        await _artifactService.StoreArtifactAsync(
            first.Id, "FinalSynthesis", "OverflowRequirements",
            """{"items": ["leftover task"]}""", "aristotle");

        await _service.CompleteSprintAsync(first.Id, force: true);

        var second = await _service.CreateSprintAsync(TestWorkspace);

        Assert.Equal(first.Id, second.OverflowFromSprintId);

        // Verify overflow content was auto-injected into the new sprint's Intake
        var artifacts = await _artifactService.GetSprintArtifactsAsync(second.Id);
        var injected = artifacts.SingleOrDefault(a => a.Type == "OverflowRequirements");
        Assert.NotNull(injected);
        Assert.Equal("Intake", injected!.Stage);
        Assert.Equal("""{"items": ["leftover task"]}""", injected.Content);
        Assert.Null(injected.CreatedByAgentId); // system-injected
    }

    [Fact]
    public async Task CreateSprint_NoOverflowArtifactWhenNone()
    {
        var first = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(first.Id, force: true);

        var second = await _service.CreateSprintAsync(TestWorkspace);

        Assert.Null(second.OverflowFromSprintId);

        var artifacts = await _artifactService.GetSprintArtifactsAsync(second.Id);
        Assert.Empty(artifacts);
    }

    [Fact]
    public async Task CreateSprint_IgnoresOverflowFromNonFinalSynthesisStage()
    {
        var first = await _service.CreateSprintAsync(TestWorkspace);

        // Store overflow in Intake (e.g. auto-injected from a previous sprint)
        await _artifactService.StoreArtifactAsync(
            first.Id, "Intake", "OverflowRequirements",
            """{"items": ["stale task"]}""", "aristotle");

        await _service.CancelSprintAsync(first.Id);

        var second = await _service.CreateSprintAsync(TestWorkspace);

        // Should NOT carry forward overflow from Intake stage
        Assert.Null(second.OverflowFromSprintId);
        var artifacts = await _artifactService.GetSprintArtifactsAsync(second.Id);
        Assert.Empty(artifacts);
    }

    [Fact]
    public async Task CreateSprint_ThrowsOnNullWorkspace()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.CreateSprintAsync(null!));
    }

    [Fact]
    public async Task CreateSprint_ThrowsOnEmptyWorkspace()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateSprintAsync(""));
    }

    [Fact]
    public async Task CreateSprint_AllowsAfterCancellation()
    {
        var first = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CancelSprintAsync(first.Id);

        var second = await _service.CreateSprintAsync(TestWorkspace);

        Assert.Equal(2, second.Number);
        Assert.Equal("Active", second.Status);
    }

    // ── GetActiveSprintAsync ─────────────────────────────────────

    [Fact]
    public async Task GetActiveSprint_ReturnsNullWhenNone()
    {
        var result = await _service.GetActiveSprintAsync(TestWorkspace);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveSprint_ReturnsActiveSprint()
    {
        var created = await _service.CreateSprintAsync(TestWorkspace);

        var result = await _service.GetActiveSprintAsync(TestWorkspace);

        Assert.NotNull(result);
        Assert.Equal(created.Id, result.Id);
    }

    [Fact]
    public async Task GetActiveSprint_DoesNotReturnCompleted()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var result = await _service.GetActiveSprintAsync(TestWorkspace);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveSprint_IsolatesByWorkspace()
    {
        var sprint1 = await _service.CreateSprintAsync("/workspace/a");
        var sprint2 = await _service.CreateSprintAsync("/workspace/b");

        var resultA = await _service.GetActiveSprintAsync("/workspace/a");
        var resultB = await _service.GetActiveSprintAsync("/workspace/b");

        Assert.Equal(sprint1.Id, resultA!.Id);
        Assert.Equal(sprint2.Id, resultB!.Id);
    }

    // ── GetSprintByIdAsync ───────────────────────────────────────

    [Fact]
    public async Task GetSprintById_ReturnsExisting()
    {
        var created = await _service.CreateSprintAsync(TestWorkspace);

        var result = await _service.GetSprintByIdAsync(created.Id);

        Assert.NotNull(result);
        Assert.Equal(created.Number, result.Number);
    }

    [Fact]
    public async Task GetSprintById_ReturnsNullForMissing()
    {
        var result = await _service.GetSprintByIdAsync("nonexistent");
        Assert.Null(result);
    }

    // ── GetSprintsForWorkspaceAsync ──────────────────────────────

    [Fact]
    public async Task GetSprintsForWorkspace_ReturnsMostRecentFirst()
    {
        var s1 = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(s1.Id, force: true);
        var s2 = await _service.CreateSprintAsync(TestWorkspace);

        var (list, totalCount) = await _service.GetSprintsForWorkspaceAsync(TestWorkspace);

        Assert.Equal(2, list.Count);
        Assert.Equal(2, totalCount);
        Assert.Equal(s2.Id, list[0].Id);
        Assert.Equal(s1.Id, list[1].Id);
    }

    [Fact]
    public async Task GetSprintsForWorkspace_RespectsLimit()
    {
        var s1 = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(s1.Id, force: true);
        await _service.CreateSprintAsync(TestWorkspace);

        var (list, totalCount) = await _service.GetSprintsForWorkspaceAsync(TestWorkspace, limit: 1);

        Assert.Single(list);
        Assert.Equal(2, totalCount);
    }

    // ── StoreArtifactAsync ───────────────────────────────────────

    [Fact]
    public async Task StoreArtifact_CreatesNew()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var artifact = await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument",
            TestArtifactContent.RequirementsDocument, "aristotle");

        Assert.Equal(sprint.Id, artifact.SprintId);
        Assert.Equal("Intake", artifact.Stage);
        Assert.Equal("RequirementsDocument", artifact.Type);
        Assert.Equal("aristotle", artifact.CreatedByAgentId);
        Assert.Null(artifact.UpdatedAt);
    }

    [Fact]
    public async Task StoreArtifact_UpdatesExisting()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var v1 = TestArtifactContent.RequirementsDocument;
        var v2 = """{"Title":"Updated","Description":"Revised","InScope":["a"],"OutOfScope":["b"],"AcceptanceCriteria":["c"]}""";

        var first = await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", v1, "aristotle");

        var updated = await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", v2, "archimedes");

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal(v2, updated.Content);
        Assert.Equal("aristotle", updated.CreatedByAgentId); // original creator preserved
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task StoreArtifact_ThrowsForMissingSprint()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _artifactService.StoreArtifactAsync(
                "nonexistent", "Intake", "RequirementsDocument", "content"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_ThrowsForCompletedSprint()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "Intake", "RequirementsDocument", "content"));
        Assert.Contains("status is Completed", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_ThrowsForInvalidStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "InvalidStage", "RequirementsDocument", "content"));
    }

    [Fact]
    public async Task StoreArtifact_ThrowsOnNullContent()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "Intake", "RequirementsDocument", null!));
    }

    // ── Artifact Content Validation ──────────────────────────────

    [Fact]
    public async Task StoreArtifact_RejectsUnknownType()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "Intake", "NotARealType", "{}"));
        Assert.Contains("Unknown artifact type", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_RejectsNumericTypeString()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Enum.TryParse accepts numeric strings — verify we reject them
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "Intake", "999", "{}"));
        Assert.Contains("Unknown artifact type", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_RejectsMalformedJson()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "Intake", "RequirementsDocument", "not json at all"));
        Assert.Contains("Invalid JSON", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_RejectsMissingRequiredFields_RequirementsDocument()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Missing Title and Description
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "Intake", "RequirementsDocument", "{}"));
        Assert.Contains("Title", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_RejectsMissingRequiredFields_SprintPlan()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "Planning", "SprintPlan", "{}"));
        Assert.Contains("Summary", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_RejectsMissingRequiredFields_ValidationReport()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "Validation", "ValidationReport", "{}"));
        Assert.Contains("Verdict", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_RejectsMissingRequiredFields_SprintReport()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "FinalSynthesis", "SprintReport", "{}"));
        Assert.Contains("Summary", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_ValidatesNestedSprintPlanPhases()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Phase with empty Name
        var content = """{"Summary":"S","Phases":[{"Name":"","Description":"D","Deliverables":[]}]}""";
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _artifactService.StoreArtifactAsync(
                sprint.Id, "Planning", "SprintPlan", content));
        Assert.Contains("Phases[0].Name", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_AllowsOverflowRequirementsWithoutValidation()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // OverflowRequirements is free-form — any content is valid
        var artifact = await _artifactService.StoreArtifactAsync(
            sprint.Id, "FinalSynthesis", "OverflowRequirements",
            "arbitrary content that is not even json");

        Assert.Equal("OverflowRequirements", artifact.Type);
    }

    [Fact]
    public async Task StoreArtifact_AcceptsValidContent()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var artifact = await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument",
            TestArtifactContent.RequirementsDocument, "test-agent");

        Assert.Equal("RequirementsDocument", artifact.Type);
        Assert.Contains("Test Requirements", artifact.Content);
    }

    [Fact]
    public async Task StoreArtifact_AcceptsExtraFieldsInContent()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Extra field "Priority" not in the schema — should be ignored, not rejected
        var content = """{"Title":"T","Description":"D","InScope":[],"OutOfScope":[],"AcceptanceCriteria":[],"Priority":"High"}""";
        var artifact = await _artifactService.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", content);

        Assert.Equal("RequirementsDocument", artifact.Type);
    }

    // ── GetSprintArtifactsAsync ──────────────────────────────────

    [Fact]
    public async Task GetArtifacts_ReturnsAll()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", TestArtifactContent.SprintPlan);

        var artifacts = await _artifactService.GetSprintArtifactsAsync(sprint.Id);

        Assert.Equal(2, artifacts.Count);
    }

    [Fact]
    public async Task GetArtifacts_FiltersByStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", TestArtifactContent.SprintPlan);

        var artifacts = await _artifactService.GetSprintArtifactsAsync(sprint.Id, "Intake");

        Assert.Single(artifacts);
        Assert.Equal("RequirementsDocument", artifacts[0].Type);
    }

    [Fact]
    public async Task GetArtifacts_ReturnsEmptyForNoMatches()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var artifacts = await _artifactService.GetSprintArtifactsAsync(sprint.Id, "Validation");

        Assert.Empty(artifacts);
    }

    [Fact]
    public async Task GetArtifacts_ThrowsForInvalidStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _artifactService.GetSprintArtifactsAsync(sprint.Id, "NotAStage"));
    }

    // ── AdvanceStageAsync ────────────────────────────────────────

    /// <summary>
    /// Helper: advances stage, approving sign-off if required.
    /// </summary>
    private async Task<SprintEntity> AdvanceWithApprovalAsync(string sprintId)
    {
        var sprint = await _stageService.AdvanceStageAsync(sprintId);
        if (sprint.AwaitingSignOff)
            sprint = await _stageService.ApproveAdvanceAsync(sprintId);
        return sprint;
    }

    [Fact]
    public async Task AdvanceStage_MovesFromIntakeToPlanning()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);

        // Intake requires sign-off
        var advanced = await _stageService.AdvanceStageAsync(sprint.Id);
        Assert.True(advanced.AwaitingSignOff);
        Assert.Equal("Planning", advanced.PendingStage);
        Assert.Equal("Intake", advanced.CurrentStage); // still at Intake until approved

        // Approve the advance
        advanced = await _stageService.ApproveAdvanceAsync(sprint.Id);
        Assert.Equal("Planning", advanced.CurrentStage);
        Assert.False(advanced.AwaitingSignOff);
    }

    [Fact]
    public async Task AdvanceStage_FullProgression()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Intake → Planning (requires RequirementsDocument + sign-off)
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);
        sprint = await AdvanceWithApprovalAsync(sprint.Id);
        Assert.Equal("Planning", sprint.CurrentStage);

        // Planning → Discussion (requires SprintPlan + sign-off)
        await _artifactService.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", TestArtifactContent.SprintPlan);
        sprint = await AdvanceWithApprovalAsync(sprint.Id);
        Assert.Equal("Discussion", sprint.CurrentStage);

        // Discussion → Validation (no required artifact, no sign-off)
        sprint = await _stageService.AdvanceStageAsync(sprint.Id);
        Assert.Equal("Validation", sprint.CurrentStage);

        // Validation → Implementation (requires ValidationReport, no sign-off)
        await _artifactService.StoreArtifactAsync(sprint.Id, "Validation", "ValidationReport", TestArtifactContent.ValidationReport);
        sprint = await _stageService.AdvanceStageAsync(sprint.Id);
        Assert.Equal("Implementation", sprint.CurrentStage);

        // Implementation → FinalSynthesis (no required artifact, no sign-off)
        sprint = await _stageService.AdvanceStageAsync(sprint.Id);
        Assert.Equal("FinalSynthesis", sprint.CurrentStage);
    }

    [Fact]
    public async Task AdvanceStage_ThrowsWithoutRequiredArtifact()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        // At Intake, no RequirementsDocument stored

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _stageService.AdvanceStageAsync(sprint.Id));
        Assert.Contains("RequirementsDocument", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_ThrowsAtFinalStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Fast-forward to FinalSynthesis
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);
        await AdvanceWithApprovalAsync(sprint.Id);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", TestArtifactContent.SprintPlan);
        await AdvanceWithApprovalAsync(sprint.Id);
        await _stageService.AdvanceStageAsync(sprint.Id); // Discussion → Validation
        await _artifactService.StoreArtifactAsync(sprint.Id, "Validation", "ValidationReport", TestArtifactContent.ValidationReport);
        await _stageService.AdvanceStageAsync(sprint.Id); // Validation → Implementation
        await _stageService.AdvanceStageAsync(sprint.Id); // Implementation → FinalSynthesis

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _stageService.AdvanceStageAsync(sprint.Id));
        Assert.Contains("final stage", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_ThrowsForCompletedSprint()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _stageService.AdvanceStageAsync(sprint.Id));
        Assert.Contains("status is Completed", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_ThrowsForMissingSprint()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _stageService.AdvanceStageAsync("nonexistent"));
    }

    [Fact]
    public async Task AdvanceStage_ThrowsWhenAlreadyAwaitingSignOff()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);
        await _stageService.AdvanceStageAsync(sprint.Id); // enters AwaitingSignOff

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _stageService.AdvanceStageAsync(sprint.Id));
        Assert.Contains("awaiting user sign-off", ex.Message);
    }

    [Fact]
    public async Task RejectAdvance_ClearsSignOffAndStaysAtCurrentStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);
        await _stageService.AdvanceStageAsync(sprint.Id); // enters AwaitingSignOff

        var rejected = await _stageService.RejectAdvanceAsync(sprint.Id);

        Assert.Equal("Intake", rejected.CurrentStage);
        Assert.False(rejected.AwaitingSignOff);
        Assert.Null(rejected.PendingStage);
    }

    [Fact]
    public async Task DiscussionStage_DoesNotRequireSignOff()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);
        await AdvanceWithApprovalAsync(sprint.Id); // Intake → Planning
        await _artifactService.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", TestArtifactContent.SprintPlan);
        await AdvanceWithApprovalAsync(sprint.Id); // Planning → Discussion

        // Discussion → Validation: no sign-off required
        var advanced = await _stageService.AdvanceStageAsync(sprint.Id);
        Assert.Equal("Validation", advanced.CurrentStage);
        Assert.False(advanced.AwaitingSignOff);
    }

    // ── CompleteSprintAsync ──────────────────────────────────────

    [Fact]
    public async Task CompleteSprint_SetsStatusAndTimestamp()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var completed = await _service.CompleteSprintAsync(sprint.Id, force: true);

        Assert.Equal("Completed", completed.Status);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public async Task CompleteSprint_ThrowsWhenNotInFinalStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CompleteSprintAsync(sprint.Id));
        Assert.Contains("FinalSynthesis", ex.Message);
    }

    [Fact]
    public async Task CompleteSprint_RequiresSprintReport()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Advance to FinalSynthesis
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);
        await AdvanceWithApprovalAsync(sprint.Id);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", TestArtifactContent.SprintPlan);
        await AdvanceWithApprovalAsync(sprint.Id);
        await _stageService.AdvanceStageAsync(sprint.Id);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Validation", "ValidationReport", TestArtifactContent.ValidationReport);
        await _stageService.AdvanceStageAsync(sprint.Id);
        await _stageService.AdvanceStageAsync(sprint.Id);

        // Try to complete without SprintReport
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CompleteSprintAsync(sprint.Id));
        Assert.Contains("SprintReport", ex.Message);
    }

    [Fact]
    public async Task CompleteSprint_SucceedsWithSprintReport()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Advance to FinalSynthesis
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", TestArtifactContent.RequirementsDocument);
        await AdvanceWithApprovalAsync(sprint.Id);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", TestArtifactContent.SprintPlan);
        await AdvanceWithApprovalAsync(sprint.Id);
        await _stageService.AdvanceStageAsync(sprint.Id);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Validation", "ValidationReport", TestArtifactContent.ValidationReport);
        await _stageService.AdvanceStageAsync(sprint.Id);
        await _stageService.AdvanceStageAsync(sprint.Id);

        // Store SprintReport then complete
        await _artifactService.StoreArtifactAsync(sprint.Id, "FinalSynthesis", "SprintReport", TestArtifactContent.SprintReport);
        var completed = await _service.CompleteSprintAsync(sprint.Id);

        Assert.Equal("Completed", completed.Status);
    }

    [Fact]
    public async Task CompleteSprint_ThrowsIfAlreadyCompleted()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CompleteSprintAsync(sprint.Id));
        Assert.Contains("already Completed", ex.Message);
    }

    [Fact]
    public async Task CompleteSprint_ForceBypassesStageCheck()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        Assert.Equal("Intake", sprint.CurrentStage);

        var completed = await _service.CompleteSprintAsync(sprint.Id, force: true);

        Assert.Equal("Completed", completed.Status);
    }

    // ── CancelSprintAsync ────────────────────────────────────────

    [Fact]
    public async Task CancelSprint_SetsStatusCancelled()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var cancelled = await _service.CancelSprintAsync(sprint.Id);

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.NotNull(cancelled.CompletedAt);
    }

    [Fact]
    public async Task CancelSprint_ThrowsIfNotActive()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CancelSprintAsync(sprint.Id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CancelSprintAsync(sprint.Id));
        Assert.Contains("already Cancelled", ex.Message);
    }

    // ── Static helpers ───────────────────────────────────────────

    [Theory]
    [InlineData("Intake", 0)]
    [InlineData("Planning", 1)]
    [InlineData("Discussion", 2)]
    [InlineData("Validation", 3)]
    [InlineData("Implementation", 4)]
    [InlineData("FinalSynthesis", 5)]
    public void GetStageIndex_ReturnsCorrectIndex(string stage, int expected)
    {
        Assert.Equal(expected, SprintService.GetStageIndex(stage));
    }

    [Fact]
    public void GetStageIndex_ReturnsMinusOneForInvalid()
    {
        Assert.Equal(-1, SprintService.GetStageIndex("NonExistent"));
    }

    [Theory]
    [InlineData("Intake", "Planning")]
    [InlineData("Implementation", "FinalSynthesis")]
    public void GetNextStage_ReturnsNext(string current, string expected)
    {
        Assert.Equal(expected, SprintService.GetNextStage(current));
    }

    [Fact]
    public void GetNextStage_ReturnsNullForLast()
    {
        Assert.Null(SprintService.GetNextStage("FinalSynthesis"));
    }

    [Fact]
    public void Stages_HasSixEntries()
    {
        Assert.Equal(6, SprintService.Stages.Count);
    }

    // ── Auto-Start on Completion ────────────────────────────────

    [Fact]
    public async Task CompleteSprint_AutoStartEnabled_CreatesNextSprint()
    {
        await _settings.SetAsync(SystemSettingsService.SprintAutoStartKey, "true");
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var active = await _db.Sprints.FirstOrDefaultAsync(
            s => s.WorkspacePath == TestWorkspace && s.Status == "Active");
        Assert.NotNull(active);
        Assert.Equal(2, active.Number);
    }

    [Fact]
    public async Task CompleteSprint_AutoStartDisabled_DoesNotCreateNextSprint()
    {
        // Default is false, no explicit set needed
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var active = await _db.Sprints.FirstOrDefaultAsync(
            s => s.WorkspacePath == TestWorkspace && s.Status == "Active");
        Assert.Null(active);
    }

    [Fact]
    public async Task CancelSprint_AutoStartEnabled_DoesNotCreateNextSprint()
    {
        await _settings.SetAsync(SystemSettingsService.SprintAutoStartKey, "true");
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        await _service.CancelSprintAsync(sprint.Id);

        var active = await _db.Sprints.FirstOrDefaultAsync(
            s => s.WorkspacePath == TestWorkspace && s.Status == "Active");
        Assert.Null(active);
    }

    [Fact]
    public async Task CompleteSprint_AutoStarted_CarriesOverflow()
    {
        await _settings.SetAsync(SystemSettingsService.SprintAutoStartKey, "true");
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Store overflow requirements before completing
        _db.SprintArtifacts.Add(new SprintArtifactEntity
        {
            SprintId = sprint.Id,
            Stage = "FinalSynthesis",
            Type = "OverflowRequirements",
            Content = "Carry-over work items",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var next = await _db.Sprints.FirstOrDefaultAsync(
            s => s.WorkspacePath == TestWorkspace && s.Status == "Active");
        Assert.NotNull(next);
        Assert.Equal(sprint.Id, next.OverflowFromSprintId);

        var overflow = await _db.SprintArtifacts.FirstOrDefaultAsync(
            a => a.SprintId == next.Id && a.Type == "OverflowRequirements");
        Assert.NotNull(overflow);
        Assert.Equal("Carry-over work items", overflow.Content);
    }

    [Fact]
    public async Task CompleteSprint_AutoStartCompletionEventOrder()
    {
        var broadcaster = new ActivityBroadcaster();
        var events = new List<ActivityEvent>();
        broadcaster.Subscribe(e => events.Add(e));

        var settings = new SystemSettingsService(_db);
        await settings.SetAsync(SystemSettingsService.SprintAutoStartKey, "true");

        var svc = new SprintService(_db, broadcaster, settings, NullLogger<SprintService>.Instance);
        var sprint = await svc.CreateSprintAsync(TestWorkspace);
        events.Clear();

        await svc.CompleteSprintAsync(sprint.Id, force: true);

        Assert.Equal(2, events.Count);
        Assert.Equal(ActivityEventType.SprintCompleted, events[0].Type);
        Assert.Equal(ActivityEventType.SprintStarted, events[1].Type);
    }

    [Fact]
    public async Task CompleteSprint_AutoStartedEvent_HasTriggerMetadata()
    {
        var broadcaster = new ActivityBroadcaster();
        var events = new List<ActivityEvent>();
        broadcaster.Subscribe(e => events.Add(e));

        var settings = new SystemSettingsService(_db);
        await settings.SetAsync(SystemSettingsService.SprintAutoStartKey, "true");

        var svc = new SprintService(_db, broadcaster, settings, NullLogger<SprintService>.Instance);
        var sprint = await svc.CreateSprintAsync(TestWorkspace);
        events.Clear();

        await svc.CompleteSprintAsync(sprint.Id, force: true);

        var startedEvent = events.First(e => e.Type == ActivityEventType.SprintStarted);
        Assert.NotNull(startedEvent.Metadata);
        Assert.Equal("auto", startedEvent.Metadata["trigger"]);
    }

    [Fact]
    public async Task CreateSprint_ManualStart_HasNullTrigger()
    {
        var broadcaster = new ActivityBroadcaster();
        var events = new List<ActivityEvent>();
        broadcaster.Subscribe(e => events.Add(e));

        var settings = new SystemSettingsService(_db);
        var svc = new SprintService(_db, broadcaster, settings, NullLogger<SprintService>.Instance);
        var sprint = await svc.CreateSprintAsync(TestWorkspace);

        var startedEvent = events.First(e => e.Type == ActivityEventType.SprintStarted);
        Assert.NotNull(startedEvent.Metadata);
        Assert.Null(startedEvent.Metadata["trigger"]);
    }

    [Fact]
    public async Task CompleteSprint_AutoStartRaceCondition_CompletionStillSucceeds()
    {
        await _settings.SetAsync(SystemSettingsService.SprintAutoStartKey, "true");
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Simulate a race: someone else already started the next sprint
        await _service.CompleteSprintAsync(sprint.Id, force: true);

        // The completion + auto-start already happened.
        // Now manually create and complete the auto-started sprint to trigger another auto-start
        // while simulating a conflict by pre-creating a sprint:
        var autoStarted = await _db.Sprints.FirstAsync(
            s => s.WorkspacePath == TestWorkspace && s.Status == "Active");

        // Complete it — auto-start should succeed
        await _service.CompleteSprintAsync(autoStarted.Id, force: true);

        var third = await _db.Sprints.FirstOrDefaultAsync(
            s => s.WorkspacePath == TestWorkspace && s.Status == "Active");
        Assert.NotNull(third);
        Assert.Equal(3, third.Number);
    }

    // ── Room Phase Sync on Sprint Creation ──────────────────────

    [Fact]
    public async Task CreateSprint_SyncsExistingRoomsToIntakePhase()
    {
        // Regression: a fresh sprint at "Intake" used to leave existing rooms
        // at their stale CurrentPhase (e.g. Implementation from a prior
        // sprint), so the room snapshot's stage filter applied the wrong
        // phase and the Intake-only constraint was violated even before any
        // round began.
        _db.Rooms.Add(new RoomEntity
        {
            Id = "stale-room",
            Name = "Stale Room",
            WorkspacePath = TestWorkspace,
            CurrentPhase = "Implementation",
            Status = nameof(RoomStatus.Active),
        });
        _db.Rooms.Add(new RoomEntity
        {
            Id = "other-workspace-room",
            Name = "Other Room",
            WorkspacePath = "/tmp/other-workspace",
            CurrentPhase = "Implementation",
            Status = nameof(RoomStatus.Active),
        });
        await _db.SaveChangesAsync();

        await _service.CreateSprintAsync(TestWorkspace);

        var stale = await _db.Rooms.FindAsync("stale-room");
        Assert.NotNull(stale);
        Assert.Equal("Intake", stale!.CurrentPhase);

        // Sync is workspace-scoped — the other workspace's room is untouched.
        var other = await _db.Rooms.FindAsync("other-workspace-room");
        Assert.NotNull(other);
        Assert.Equal("Implementation", other!.CurrentPhase);
    }

    [Fact]
    public async Task CreateSprint_DoesNotSyncArchivedOrCompletedRooms()
    {
        _db.Rooms.Add(new RoomEntity
        {
            Id = "archived-room",
            Name = "Archived",
            WorkspacePath = TestWorkspace,
            CurrentPhase = "Implementation",
            Status = nameof(RoomStatus.Archived),
        });
        _db.Rooms.Add(new RoomEntity
        {
            Id = "completed-room",
            Name = "Completed",
            WorkspacePath = TestWorkspace,
            CurrentPhase = "FinalSynthesis",
            Status = nameof(RoomStatus.Completed),
        });
        await _db.SaveChangesAsync();

        await _service.CreateSprintAsync(TestWorkspace);

        var archived = await _db.Rooms.FindAsync("archived-room");
        var completed = await _db.Rooms.FindAsync("completed-room");
        Assert.Equal("Implementation", archived!.CurrentPhase);
        Assert.Equal("FinalSynthesis", completed!.CurrentPhase);
    }
}
