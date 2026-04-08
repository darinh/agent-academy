using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
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

    public SprintServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _service = new SprintService(_db, new ActivityBroadcaster(), NullLogger<SprintService>.Instance);
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
        await _service.StoreArtifactAsync(
            first.Id, "FinalSynthesis", "OverflowRequirements",
            """{"items": ["leftover task"]}""", "aristotle");

        await _service.CompleteSprintAsync(first.Id, force: true);

        var second = await _service.CreateSprintAsync(TestWorkspace);

        Assert.Equal(first.Id, second.OverflowFromSprintId);
    }

    [Fact]
    public async Task CreateSprint_NoOverflowLinkWhenNone()
    {
        var first = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(first.Id, force: true);

        var second = await _service.CreateSprintAsync(TestWorkspace);

        Assert.Null(second.OverflowFromSprintId);
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

        var artifact = await _service.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument",
            """{"requirements": ["build a widget"]}""", "aristotle");

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

        var first = await _service.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", "v1", "aristotle");

        var updated = await _service.StoreArtifactAsync(
            sprint.Id, "Intake", "RequirementsDocument", "v2", "archimedes");

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("v2", updated.Content);
        Assert.Equal("aristotle", updated.CreatedByAgentId); // original creator preserved
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task StoreArtifact_ThrowsForMissingSprint()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StoreArtifactAsync(
                "nonexistent", "Intake", "RequirementsDocument", "content"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_ThrowsForCompletedSprint()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StoreArtifactAsync(
                sprint.Id, "Intake", "RequirementsDocument", "content"));
        Assert.Contains("status is Completed", ex.Message);
    }

    [Fact]
    public async Task StoreArtifact_ThrowsForInvalidStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.StoreArtifactAsync(
                sprint.Id, "InvalidStage", "RequirementsDocument", "content"));
    }

    [Fact]
    public async Task StoreArtifact_ThrowsOnNullContent()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.StoreArtifactAsync(
                sprint.Id, "Intake", "RequirementsDocument", null!));
    }

    // ── GetSprintArtifactsAsync ──────────────────────────────────

    [Fact]
    public async Task GetArtifacts_ReturnsAll()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "r1");
        await _service.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", "p1");

        var artifacts = await _service.GetSprintArtifactsAsync(sprint.Id);

        Assert.Equal(2, artifacts.Count);
    }

    [Fact]
    public async Task GetArtifacts_FiltersByStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "r1");
        await _service.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", "p1");

        var artifacts = await _service.GetSprintArtifactsAsync(sprint.Id, "Intake");

        Assert.Single(artifacts);
        Assert.Equal("RequirementsDocument", artifacts[0].Type);
    }

    [Fact]
    public async Task GetArtifacts_ReturnsEmptyForNoMatches()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var artifacts = await _service.GetSprintArtifactsAsync(sprint.Id, "Validation");

        Assert.Empty(artifacts);
    }

    [Fact]
    public async Task GetArtifacts_ThrowsForInvalidStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.GetSprintArtifactsAsync(sprint.Id, "NotAStage"));
    }

    // ── AdvanceStageAsync ────────────────────────────────────────

    [Fact]
    public async Task AdvanceStage_MovesFromIntakeToPlanning()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "doc");

        var advanced = await _service.AdvanceStageAsync(sprint.Id);

        Assert.Equal("Planning", advanced.CurrentStage);
    }

    [Fact]
    public async Task AdvanceStage_FullProgression()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Intake → Planning (requires RequirementsDocument)
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "{}");
        sprint = await _service.AdvanceStageAsync(sprint.Id);
        Assert.Equal("Planning", sprint.CurrentStage);

        // Planning → Discussion (requires SprintPlan)
        await _service.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", "{}");
        sprint = await _service.AdvanceStageAsync(sprint.Id);
        Assert.Equal("Discussion", sprint.CurrentStage);

        // Discussion → Validation (no required artifact)
        sprint = await _service.AdvanceStageAsync(sprint.Id);
        Assert.Equal("Validation", sprint.CurrentStage);

        // Validation → Implementation (requires ValidationReport)
        await _service.StoreArtifactAsync(sprint.Id, "Validation", "ValidationReport", "{}");
        sprint = await _service.AdvanceStageAsync(sprint.Id);
        Assert.Equal("Implementation", sprint.CurrentStage);

        // Implementation → FinalSynthesis (no required artifact)
        sprint = await _service.AdvanceStageAsync(sprint.Id);
        Assert.Equal("FinalSynthesis", sprint.CurrentStage);
    }

    [Fact]
    public async Task AdvanceStage_ThrowsWithoutRequiredArtifact()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        // At Intake, no RequirementsDocument stored

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(sprint.Id));
        Assert.Contains("RequirementsDocument", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_ThrowsAtFinalStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Fast-forward to FinalSynthesis
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "{}");
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", "{}");
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.AdvanceStageAsync(sprint.Id); // Discussion → Validation
        await _service.StoreArtifactAsync(sprint.Id, "Validation", "ValidationReport", "{}");
        await _service.AdvanceStageAsync(sprint.Id); // Validation → Implementation
        await _service.AdvanceStageAsync(sprint.Id); // Implementation → FinalSynthesis

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(sprint.Id));
        Assert.Contains("final stage", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_ThrowsForCompletedSprint()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(sprint.Id));
        Assert.Contains("status is Completed", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_ThrowsForMissingSprint()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("nonexistent"));
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
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "{}");
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", "{}");
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.StoreArtifactAsync(sprint.Id, "Validation", "ValidationReport", "{}");
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.AdvanceStageAsync(sprint.Id);

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
        await _service.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument", "{}");
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan", "{}");
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.StoreArtifactAsync(sprint.Id, "Validation", "ValidationReport", "{}");
        await _service.AdvanceStageAsync(sprint.Id);
        await _service.AdvanceStageAsync(sprint.Id);

        // Store SprintReport then complete
        await _service.StoreArtifactAsync(sprint.Id, "FinalSynthesis", "SprintReport", "{}");
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
}
