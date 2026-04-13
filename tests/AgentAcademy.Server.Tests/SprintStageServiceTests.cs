using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for SprintStageService — stage advancement state machine, sign-off
/// gating, approval/rejection, timeout, and static helpers.
/// </summary>
public class SprintStageServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ActivityBroadcaster _broadcaster;
    private readonly SprintStageService _service;

    public SprintStageServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _broadcaster = new ActivityBroadcaster();
        _service = new SprintStageService(_db, _broadcaster, NullLogger<SprintStageService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private async Task<SprintEntity> SeedSprintAsync(
        string id = "sprint-1", int number = 1, string status = "Active",
        string stage = "Intake", bool awaitingSignOff = false,
        string? pendingStage = null)
    {
        var sprint = new SprintEntity
        {
            Id = id,
            Number = number,
            WorkspacePath = $"/tmp/test-{id}",
            Status = status,
            CurrentStage = stage,
            AwaitingSignOff = awaitingSignOff,
            PendingStage = pendingStage,
            SignOffRequestedAt = awaitingSignOff ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow
        };
        _db.Sprints.Add(sprint);
        await _db.SaveChangesAsync();
        return sprint;
    }

    private async Task SeedArtifactAsync(string sprintId, string stage, string type)
    {
        _db.SprintArtifacts.Add(new SprintArtifactEntity
        {
            SprintId = sprintId,
            Stage = stage,
            Type = type,
            Content = "{}",
            CreatedByAgentId = "test-agent",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    // ── Static Data ─────────────────────────────────────────────

    [Fact]
    public void Stages_ContainsSixStagesInOrder()
    {
        Assert.Equal(6, SprintStageService.Stages.Count);
        Assert.Equal("Intake", SprintStageService.Stages[0]);
        Assert.Equal("Planning", SprintStageService.Stages[1]);
        Assert.Equal("Discussion", SprintStageService.Stages[2]);
        Assert.Equal("Validation", SprintStageService.Stages[3]);
        Assert.Equal("Implementation", SprintStageService.Stages[4]);
        Assert.Equal("FinalSynthesis", SprintStageService.Stages[5]);
    }

    [Fact]
    public void RequiredArtifactByStage_MapsCorrectly()
    {
        Assert.Equal("RequirementsDocument", SprintStageService.RequiredArtifactByStage["Intake"]);
        Assert.Equal("SprintPlan", SprintStageService.RequiredArtifactByStage["Planning"]);
        Assert.Equal("ValidationReport", SprintStageService.RequiredArtifactByStage["Validation"]);
        Assert.Equal("SprintReport", SprintStageService.RequiredArtifactByStage["FinalSynthesis"]);
    }

    [Fact]
    public void RequiredArtifactByStage_NoEntryForDiscussionOrImplementation()
    {
        Assert.False(SprintStageService.RequiredArtifactByStage.ContainsKey("Discussion"));
        Assert.False(SprintStageService.RequiredArtifactByStage.ContainsKey("Implementation"));
    }

    // ── GetStageIndex ───────────────────────────────────────────

    [Theory]
    [InlineData("Intake", 0)]
    [InlineData("Planning", 1)]
    [InlineData("Discussion", 2)]
    [InlineData("Validation", 3)]
    [InlineData("Implementation", 4)]
    [InlineData("FinalSynthesis", 5)]
    public void GetStageIndex_ValidStage_ReturnsCorrectIndex(string stage, int expected)
    {
        Assert.Equal(expected, SprintStageService.GetStageIndex(stage));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("intake")] // case-sensitive
    public void GetStageIndex_InvalidStage_ReturnsNegativeOne(string stage)
    {
        Assert.Equal(-1, SprintStageService.GetStageIndex(stage));
    }

    // ── GetNextStage ────────────────────────────────────────────

    [Theory]
    [InlineData("Intake", "Planning")]
    [InlineData("Planning", "Discussion")]
    [InlineData("Discussion", "Validation")]
    [InlineData("Validation", "Implementation")]
    [InlineData("Implementation", "FinalSynthesis")]
    public void GetNextStage_ValidNonFinalStage_ReturnsNextStage(string stage, string expected)
    {
        Assert.Equal(expected, SprintStageService.GetNextStage(stage));
    }

    [Fact]
    public void GetNextStage_FinalStage_ReturnsNull()
    {
        Assert.Null(SprintStageService.GetNextStage("FinalSynthesis"));
    }

    [Fact]
    public void GetNextStage_UnknownStage_ReturnsNull()
    {
        Assert.Null(SprintStageService.GetNextStage("Unknown"));
    }

    // ── AdvanceStageAsync — Error Paths ─────────────────────────

    [Fact]
    public async Task AdvanceStage_SprintNotFound_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("nonexistent"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_CompletedSprint_Throws()
    {
        await SeedSprintAsync(status: "Completed");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("Completed", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_CancelledSprint_Throws()
    {
        await SeedSprintAsync(status: "Cancelled");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("Cancelled", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_AwaitingSignOff_Throws()
    {
        await SeedSprintAsync(awaitingSignOff: true, pendingStage: "Planning");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("awaiting user sign-off", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_UnknownStage_Throws()
    {
        await SeedSprintAsync(stage: "Bogus");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("unknown stage", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_AlreadyAtFinalStage_Throws()
    {
        await SeedSprintAsync(stage: "FinalSynthesis");
        await SeedArtifactAsync("sprint-1", "FinalSynthesis", "SprintReport");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("final stage", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_MissingRequiredArtifact_Intake_Throws()
    {
        await SeedSprintAsync(stage: "Intake");
        // No RequirementsDocument artifact

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("RequirementsDocument", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_MissingRequiredArtifact_Planning_Throws()
    {
        await SeedSprintAsync(stage: "Planning");
        // No SprintPlan artifact

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("SprintPlan", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_MissingRequiredArtifact_Validation_Throws()
    {
        await SeedSprintAsync(stage: "Validation");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("ValidationReport", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_WrongArtifactType_DoesNotSatisfyGate()
    {
        await SeedSprintAsync(stage: "Intake");
        await SeedArtifactAsync("sprint-1", "Intake", "WrongType");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("RequirementsDocument", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_ArtifactForDifferentStage_DoesNotSatisfyGate()
    {
        await SeedSprintAsync(stage: "Intake");
        await SeedArtifactAsync("sprint-1", "Planning", "RequirementsDocument");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("RequirementsDocument", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_ArtifactForDifferentSprint_DoesNotSatisfyGate()
    {
        await SeedSprintAsync(stage: "Intake");
        await SeedSprintAsync(id: "sprint-2", number: 2);
        await SeedArtifactAsync("sprint-2", "Intake", "RequirementsDocument");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
        Assert.Contains("RequirementsDocument", ex.Message);
    }

    // ── AdvanceStageAsync — Sign-off Required (Intake) ──────────

    [Fact]
    public async Task AdvanceStage_Intake_WithArtifact_EntersSignOff()
    {
        await SeedSprintAsync(stage: "Intake");
        await SeedArtifactAsync("sprint-1", "Intake", "RequirementsDocument");

        var result = await _service.AdvanceStageAsync("sprint-1");

        Assert.True(result.AwaitingSignOff);
        Assert.Equal("Planning", result.PendingStage);
        Assert.Equal("Intake", result.CurrentStage); // does NOT advance yet
        Assert.NotNull(result.SignOffRequestedAt);
    }

    [Fact]
    public async Task AdvanceStage_Intake_PersistsSignOffState()
    {
        await SeedSprintAsync(stage: "Intake");
        await SeedArtifactAsync("sprint-1", "Intake", "RequirementsDocument");

        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var reloaded = await _db.Sprints.FindAsync("sprint-1");
        Assert.NotNull(reloaded);
        Assert.True(reloaded.AwaitingSignOff);
        Assert.Equal("Planning", reloaded.PendingStage);
        Assert.Equal("Intake", reloaded.CurrentStage);
        Assert.NotNull(reloaded.SignOffRequestedAt);
    }

    [Fact]
    public async Task AdvanceStage_Intake_PersistsActivityEvent()
    {
        await SeedSprintAsync(stage: "Intake");
        await SeedArtifactAsync("sprint-1", "Intake", "RequirementsDocument");

        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var events = await _db.ActivityEvents.ToListAsync();
        Assert.Single(events);
        Assert.Contains("awaiting user sign-off", events[0].Message);
        Assert.Contains("signoff_requested", events[0].MetadataJson ?? "");
    }

    // ── AdvanceStageAsync — Sign-off Required (Planning) ────────

    [Fact]
    public async Task AdvanceStage_Planning_WithArtifact_EntersSignOff()
    {
        await SeedSprintAsync(stage: "Planning");
        await SeedArtifactAsync("sprint-1", "Planning", "SprintPlan");

        var result = await _service.AdvanceStageAsync("sprint-1");

        Assert.True(result.AwaitingSignOff);
        Assert.Equal("Discussion", result.PendingStage);
        Assert.Equal("Planning", result.CurrentStage);
        Assert.NotNull(result.SignOffRequestedAt);
    }

    [Fact]
    public async Task AdvanceStage_Planning_PersistsActivityEvent()
    {
        await SeedSprintAsync(stage: "Planning");
        await SeedArtifactAsync("sprint-1", "Planning", "SprintPlan");

        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var events = await _db.ActivityEvents.ToListAsync();
        Assert.Single(events);
        Assert.Contains("Planning", events[0].Message);
        Assert.Contains("Discussion", events[0].Message);
    }

    // ── AdvanceStageAsync — No Sign-off Required (Discussion) ───

    [Fact]
    public async Task AdvanceStage_Discussion_AdvancesDirectlyToValidation()
    {
        await SeedSprintAsync(stage: "Discussion");

        var result = await _service.AdvanceStageAsync("sprint-1");

        Assert.Equal("Validation", result.CurrentStage);
        Assert.False(result.AwaitingSignOff);
        Assert.Null(result.PendingStage);
        Assert.Null(result.SignOffRequestedAt);
    }

    [Fact]
    public async Task AdvanceStage_Discussion_PersistsNewStage()
    {
        await SeedSprintAsync(stage: "Discussion");

        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var reloaded = await _db.Sprints.FindAsync("sprint-1");
        Assert.NotNull(reloaded);
        Assert.Equal("Validation", reloaded.CurrentStage);
        Assert.False(reloaded.AwaitingSignOff);
    }

    [Fact]
    public async Task AdvanceStage_Discussion_PersistsActivityEvent()
    {
        await SeedSprintAsync(stage: "Discussion");

        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var events = await _db.ActivityEvents.ToListAsync();
        Assert.Single(events);
        Assert.Contains("advanced", events[0].Message);
        Assert.Contains("Discussion", events[0].Message);
        Assert.Contains("Validation", events[0].Message);
    }

    // ── AdvanceStageAsync — Validation with artifact (no sign-off) ─

    [Fact]
    public async Task AdvanceStage_Validation_WithArtifact_AdvancesDirectly()
    {
        await SeedSprintAsync(stage: "Validation");
        await SeedArtifactAsync("sprint-1", "Validation", "ValidationReport");

        var result = await _service.AdvanceStageAsync("sprint-1");

        Assert.Equal("Implementation", result.CurrentStage);
        Assert.False(result.AwaitingSignOff);
        Assert.Null(result.PendingStage);
    }

    [Fact]
    public async Task AdvanceStage_Validation_PersistsActivityEvent()
    {
        await SeedSprintAsync(stage: "Validation");
        await SeedArtifactAsync("sprint-1", "Validation", "ValidationReport");

        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var events = await _db.ActivityEvents.ToListAsync();
        Assert.Single(events);
        Assert.Contains("advanced", events[0].MetadataJson ?? "");
    }

    // ── AdvanceStageAsync — Implementation (no artifact, no sign-off) ─

    [Fact]
    public async Task AdvanceStage_Implementation_AdvancesDirectly()
    {
        await SeedSprintAsync(stage: "Implementation");

        var result = await _service.AdvanceStageAsync("sprint-1");

        Assert.Equal("FinalSynthesis", result.CurrentStage);
        Assert.False(result.AwaitingSignOff);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_PersistsActivityEvent()
    {
        await SeedSprintAsync(stage: "Implementation");

        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.Contains("Implementation", evt.Message);
        Assert.Contains("FinalSynthesis", evt.Message);
    }

    // ── AdvanceStageAsync — Event content verification ──────────

    [Fact]
    public async Task AdvanceStage_SignOffEvent_HasCorrectMetadata()
    {
        await SeedSprintAsync(stage: "Intake");
        await SeedArtifactAsync("sprint-1", "Intake", "RequirementsDocument");

        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.Equal("SprintStageAdvanced", evt.Type);
        Assert.NotNull(evt.MetadataJson);
        Assert.Contains("\"awaitingSignOff\":true", evt.MetadataJson);
        Assert.Contains("\"pendingStage\":\"Planning\"", evt.MetadataJson);
        Assert.Contains("\"currentStage\":\"Intake\"", evt.MetadataJson);
    }

    [Fact]
    public async Task AdvanceStage_DirectAdvanceEvent_HasCorrectMetadata()
    {
        await SeedSprintAsync(stage: "Discussion");

        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.Equal("SprintStageAdvanced", evt.Type);
        Assert.NotNull(evt.MetadataJson);
        Assert.Contains("\"awaitingSignOff\":false", evt.MetadataJson);
        Assert.Contains("\"previousStage\":\"Discussion\"", evt.MetadataJson);
        Assert.Contains("\"currentStage\":\"Validation\"", evt.MetadataJson);
    }

    // ── ApproveAdvanceAsync ─────────────────────────────────────

    [Fact]
    public async Task ApproveAdvance_SprintNotFound_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ApproveAdvanceAsync("nonexistent"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ApproveAdvance_NotAwaitingSignOff_Throws()
    {
        await SeedSprintAsync(awaitingSignOff: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ApproveAdvanceAsync("sprint-1"));
        Assert.Contains("not awaiting sign-off", ex.Message);
    }

    [Fact]
    public async Task ApproveAdvance_AwaitingButPendingStageNull_Throws()
    {
        // Edge case: AwaitingSignOff = true but PendingStage is null
        await SeedSprintAsync(awaitingSignOff: true, pendingStage: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ApproveAdvanceAsync("sprint-1"));
        Assert.Contains("not awaiting sign-off", ex.Message);
    }

    [Fact]
    public async Task ApproveAdvance_Success_AdvancesStage()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        var result = await _service.ApproveAdvanceAsync("sprint-1");

        Assert.Equal("Planning", result.CurrentStage);
        Assert.False(result.AwaitingSignOff);
        Assert.Null(result.PendingStage);
        Assert.Null(result.SignOffRequestedAt);
    }

    [Fact]
    public async Task ApproveAdvance_Success_PersistsState()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        await _service.ApproveAdvanceAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var reloaded = await _db.Sprints.FindAsync("sprint-1");
        Assert.NotNull(reloaded);
        Assert.Equal("Planning", reloaded.CurrentStage);
        Assert.False(reloaded.AwaitingSignOff);
        Assert.Null(reloaded.PendingStage);
        Assert.Null(reloaded.SignOffRequestedAt);
    }

    [Fact]
    public async Task ApproveAdvance_Success_PersistsActivityEvent()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        await _service.ApproveAdvanceAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.Equal("SprintStageAdvanced", evt.Type);
        Assert.Contains("approved", evt.Message);
        Assert.Contains("Intake", evt.Message);
        Assert.Contains("Planning", evt.Message);
    }

    [Fact]
    public async Task ApproveAdvance_EventMetadata_ContainsApprovedAction()
    {
        await SeedSprintAsync(stage: "Planning", awaitingSignOff: true, pendingStage: "Discussion");

        await _service.ApproveAdvanceAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.NotNull(evt.MetadataJson);
        Assert.Contains("\"action\":\"approved\"", evt.MetadataJson);
        Assert.Contains("\"previousStage\":\"Planning\"", evt.MetadataJson);
        Assert.Contains("\"currentStage\":\"Discussion\"", evt.MetadataJson);
    }

    // ── RejectAdvanceAsync ──────────────────────────────────────

    [Fact]
    public async Task RejectAdvance_SprintNotFound_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RejectAdvanceAsync("nonexistent"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task RejectAdvance_NotAwaitingSignOff_Throws()
    {
        await SeedSprintAsync(awaitingSignOff: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RejectAdvanceAsync("sprint-1"));
        Assert.Contains("not awaiting sign-off", ex.Message);
    }

    [Fact]
    public async Task RejectAdvance_Success_KeepsCurrentStage()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        var result = await _service.RejectAdvanceAsync("sprint-1");

        Assert.Equal("Intake", result.CurrentStage); // stays at Intake
        Assert.False(result.AwaitingSignOff);
        Assert.Null(result.PendingStage);
        Assert.Null(result.SignOffRequestedAt);
    }

    [Fact]
    public async Task RejectAdvance_Success_PersistsState()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        await _service.RejectAdvanceAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var reloaded = await _db.Sprints.FindAsync("sprint-1");
        Assert.NotNull(reloaded);
        Assert.Equal("Intake", reloaded.CurrentStage);
        Assert.False(reloaded.AwaitingSignOff);
        Assert.Null(reloaded.PendingStage);
        Assert.Null(reloaded.SignOffRequestedAt);
    }

    [Fact]
    public async Task RejectAdvance_Success_PersistsActivityEvent()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        await _service.RejectAdvanceAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.Equal("SprintStageAdvanced", evt.Type);
        Assert.Contains("rejected", evt.Message);
        Assert.Contains("Intake", evt.Message);
    }

    [Fact]
    public async Task RejectAdvance_EventMetadata_ContainsRejectedAction()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        await _service.RejectAdvanceAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.NotNull(evt.MetadataJson);
        Assert.Contains("\"action\":\"rejected\"", evt.MetadataJson);
        Assert.Contains("\"currentStage\":\"Intake\"", evt.MetadataJson);
    }

    [Fact]
    public async Task RejectAdvance_PlanningStage_KeepsPlanningStage()
    {
        await SeedSprintAsync(stage: "Planning", awaitingSignOff: true, pendingStage: "Discussion");

        var result = await _service.RejectAdvanceAsync("sprint-1");

        Assert.Equal("Planning", result.CurrentStage);
        Assert.False(result.AwaitingSignOff);
    }

    // ── TimeOutSignOffAsync ─────────────────────────────────────

    [Fact]
    public async Task TimeOutSignOff_SprintNotFound_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.TimeOutSignOffAsync("nonexistent"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task TimeOutSignOff_NotAwaitingSignOff_Throws()
    {
        await SeedSprintAsync(awaitingSignOff: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.TimeOutSignOffAsync("sprint-1"));
        Assert.Contains("not awaiting sign-off", ex.Message);
    }

    [Fact]
    public async Task TimeOutSignOff_Success_ClearsSignOffState()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        var result = await _service.TimeOutSignOffAsync("sprint-1");

        Assert.Equal("Intake", result.CurrentStage); // stays at current
        Assert.False(result.AwaitingSignOff);
        Assert.Null(result.PendingStage);
        Assert.Null(result.SignOffRequestedAt);
    }

    [Fact]
    public async Task TimeOutSignOff_Success_PersistsState()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        await _service.TimeOutSignOffAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var reloaded = await _db.Sprints.FindAsync("sprint-1");
        Assert.NotNull(reloaded);
        Assert.Equal("Intake", reloaded.CurrentStage);
        Assert.False(reloaded.AwaitingSignOff);
        Assert.Null(reloaded.PendingStage);
        Assert.Null(reloaded.SignOffRequestedAt);
    }

    [Fact]
    public async Task TimeOutSignOff_PersistsActivityEvent_WithTimeoutReason()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        await _service.TimeOutSignOffAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.Equal("SprintStageAdvanced", evt.Type);
        Assert.Contains("timed out", evt.Message);
        Assert.NotNull(evt.MetadataJson);
        Assert.Contains("\"reason\":\"timeout\"", evt.MetadataJson);
        Assert.Contains("\"action\":\"timeout_rejected\"", evt.MetadataJson);
    }

    [Fact]
    public async Task TimeOutSignOff_EventMetadata_ContainsPendingStage()
    {
        await SeedSprintAsync(stage: "Planning", awaitingSignOff: true, pendingStage: "Discussion");

        await _service.TimeOutSignOffAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.NotNull(evt.MetadataJson);
        Assert.Contains("\"pendingStage\":\"Discussion\"", evt.MetadataJson);
        Assert.Contains("\"currentStage\":\"Planning\"", evt.MetadataJson);
    }

    [Fact]
    public async Task TimeOutSignOff_WithCancellationTokenNone_Succeeds()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        var result = await _service.TimeOutSignOffAsync("sprint-1", CancellationToken.None);

        Assert.Equal("Intake", result.CurrentStage);
        Assert.False(result.AwaitingSignOff);
    }

    [Fact]
    public async Task TimeOutSignOff_WithCancelledToken_ThrowsOperationCancelled()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // FindAsync or SaveChangesAsync should throw on a cancelled token
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.TimeOutSignOffAsync("sprint-1", cts.Token));
    }

    // ── Full Stage Progression ──────────────────────────────────

    [Fact]
    public async Task FullProgression_IntakeThroughImplementation()
    {
        await SeedSprintAsync(stage: "Intake");

        // Intake → needs artifact + sign-off
        await SeedArtifactAsync("sprint-1", "Intake", "RequirementsDocument");
        var s1 = await _service.AdvanceStageAsync("sprint-1");
        Assert.True(s1.AwaitingSignOff);
        Assert.Equal("Intake", s1.CurrentStage);

        // Approve sign-off
        var s2 = await _service.ApproveAdvanceAsync("sprint-1");
        Assert.Equal("Planning", s2.CurrentStage);

        // Planning → needs artifact + sign-off
        await SeedArtifactAsync("sprint-1", "Planning", "SprintPlan");
        var s3 = await _service.AdvanceStageAsync("sprint-1");
        Assert.True(s3.AwaitingSignOff);
        Assert.Equal("Planning", s3.CurrentStage);

        // Approve sign-off
        var s4 = await _service.ApproveAdvanceAsync("sprint-1");
        Assert.Equal("Discussion", s4.CurrentStage);

        // Discussion → no artifact, no sign-off
        var s5 = await _service.AdvanceStageAsync("sprint-1");
        Assert.Equal("Validation", s5.CurrentStage);

        // Validation → needs artifact, no sign-off
        await SeedArtifactAsync("sprint-1", "Validation", "ValidationReport");
        var s6 = await _service.AdvanceStageAsync("sprint-1");
        Assert.Equal("Implementation", s6.CurrentStage);

        // Implementation → no artifact, no sign-off
        var s7 = await _service.AdvanceStageAsync("sprint-1");
        Assert.Equal("FinalSynthesis", s7.CurrentStage);

        // FinalSynthesis is final — can't advance further
        await SeedArtifactAsync("sprint-1", "FinalSynthesis", "SprintReport");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));
    }

    [Fact]
    public async Task FullProgression_ActivityEventsAccumulate()
    {
        await SeedSprintAsync(stage: "Discussion");

        await _service.AdvanceStageAsync("sprint-1"); // Discussion → Validation
        await SeedArtifactAsync("sprint-1", "Validation", "ValidationReport");
        await _service.AdvanceStageAsync("sprint-1"); // Validation → Implementation
        await _service.AdvanceStageAsync("sprint-1"); // Implementation → FinalSynthesis

        var count = await _db.ActivityEvents.CountAsync();
        Assert.Equal(3, count);
    }

    // ── Reject then Re-advance ──────────────────────────────────

    [Fact]
    public async Task RejectThenReAdvance_CanRequestSignOffAgain()
    {
        await SeedSprintAsync(stage: "Intake");
        await SeedArtifactAsync("sprint-1", "Intake", "RequirementsDocument");

        // First advance → enters sign-off
        await _service.AdvanceStageAsync("sprint-1");

        // Reject
        var rejected = await _service.RejectAdvanceAsync("sprint-1");
        Assert.Equal("Intake", rejected.CurrentStage);
        Assert.False(rejected.AwaitingSignOff);

        // Re-advance → enters sign-off again
        var reAdvanced = await _service.AdvanceStageAsync("sprint-1");
        Assert.True(reAdvanced.AwaitingSignOff);
        Assert.Equal("Planning", reAdvanced.PendingStage);
    }

    // ── Timeout then Re-advance ─────────────────────────────────

    [Fact]
    public async Task TimeoutThenReAdvance_CanRequestSignOffAgain()
    {
        await SeedSprintAsync(stage: "Planning");
        await SeedArtifactAsync("sprint-1", "Planning", "SprintPlan");

        // Advance → enters sign-off
        await _service.AdvanceStageAsync("sprint-1");

        // Timeout
        var timedOut = await _service.TimeOutSignOffAsync("sprint-1");
        Assert.Equal("Planning", timedOut.CurrentStage);
        Assert.False(timedOut.AwaitingSignOff);

        // Re-advance → enters sign-off again
        var reAdvanced = await _service.AdvanceStageAsync("sprint-1");
        Assert.True(reAdvanced.AwaitingSignOff);
        Assert.Equal("Discussion", reAdvanced.PendingStage);
    }

    // ── Multiple Sprints Isolation ──────────────────────────────

    [Fact]
    public async Task MultipleSprints_ArtifactIsolation()
    {
        await SeedSprintAsync(id: "s-1", number: 1, stage: "Intake");
        await SeedSprintAsync(id: "s-2", number: 2, stage: "Intake");
        await SeedArtifactAsync("s-2", "Intake", "RequirementsDocument");

        // s-1 has no artifact → should fail
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("s-1"));

        // s-2 has artifact → should succeed (enter sign-off)
        var result = await _service.AdvanceStageAsync("s-2");
        Assert.True(result.AwaitingSignOff);
    }

    [Fact]
    public async Task MultipleSprints_IndependentSignOff()
    {
        await SeedSprintAsync(id: "s-1", number: 1, stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");
        await SeedSprintAsync(id: "s-2", number: 2, stage: "Discussion");

        // Approve s-1
        var s1 = await _service.ApproveAdvanceAsync("s-1");
        Assert.Equal("Planning", s1.CurrentStage);

        // s-2 can advance independently
        var s2 = await _service.AdvanceStageAsync("s-2");
        Assert.Equal("Validation", s2.CurrentStage);
    }

    // ── Event Severity and Type ─────────────────────────────────

    [Fact]
    public async Task AllEvents_HaveInfoSeverity()
    {
        await SeedSprintAsync(stage: "Discussion");
        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.Equal("Info", evt.Severity);
    }

    [Fact]
    public async Task AllEvents_HaveSprintStageAdvancedType()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");
        await _service.ApproveAdvanceAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.Equal("SprintStageAdvanced", evt.Type);
    }

    [Fact]
    public async Task AllEvents_HaveNonEmptyId()
    {
        await SeedSprintAsync(stage: "Discussion");
        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.NotNull(evt.Id);
        Assert.NotEmpty(evt.Id);
    }

    [Fact]
    public async Task AllEvents_HaveRecentTimestamp()
    {
        var before = DateTime.UtcNow.AddSeconds(-5);
        await SeedSprintAsync(stage: "Discussion");
        await _service.AdvanceStageAsync("sprint-1");

        _db.ChangeTracker.Clear();
        var evt = await _db.ActivityEvents.SingleAsync();
        Assert.True(evt.OccurredAt >= before);
        Assert.True(evt.OccurredAt <= DateTime.UtcNow.AddSeconds(5));
    }

    // ── Return value verification ───────────────────────────────

    [Fact]
    public async Task AdvanceStage_ReturnsSameEntityAsDatabase()
    {
        await SeedSprintAsync(stage: "Discussion");

        var returned = await _service.AdvanceStageAsync("sprint-1");
        var fromDb = await _db.Sprints.FindAsync("sprint-1");

        Assert.NotNull(fromDb);
        Assert.Equal(fromDb.CurrentStage, returned.CurrentStage);
        Assert.Equal(fromDb.AwaitingSignOff, returned.AwaitingSignOff);
        Assert.Equal(fromDb.PendingStage, returned.PendingStage);
    }

    [Fact]
    public async Task ApproveAdvance_ReturnsSameEntityAsDatabase()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        var returned = await _service.ApproveAdvanceAsync("sprint-1");
        var fromDb = await _db.Sprints.FindAsync("sprint-1");

        Assert.NotNull(fromDb);
        Assert.Equal(fromDb.CurrentStage, returned.CurrentStage);
        Assert.Equal(fromDb.AwaitingSignOff, returned.AwaitingSignOff);
    }

    [Fact]
    public async Task RejectAdvance_ReturnsSameEntityAsDatabase()
    {
        await SeedSprintAsync(stage: "Intake", awaitingSignOff: true, pendingStage: "Planning");

        var returned = await _service.RejectAdvanceAsync("sprint-1");
        var fromDb = await _db.Sprints.FindAsync("sprint-1");

        Assert.NotNull(fromDb);
        Assert.Equal(fromDb.CurrentStage, returned.CurrentStage);
        Assert.Equal(fromDb.AwaitingSignOff, returned.AwaitingSignOff);
    }

    // ── Stage Prerequisites ─────────────────────────────────────

    private void SeedTask(string sprintId, string status, string title = "Test task", string? id = null)
    {
        const string roomId = "room-prereq";
        if (!_db.Rooms.Any(r => r.Id == roomId))
        {
            _db.Rooms.Add(new RoomEntity
            {
                Id = roomId,
                Name = "Prereq Test Room",
                Status = "Active",
                WorkspacePath = "/tmp/test-sprint-1",
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }

        _db.Tasks.Add(new TaskEntity
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Title = title,
            Description = "Test",
            SuccessCriteria = "Test",
            Status = status,
            RoomId = roomId,
            SprintId = sprintId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task AdvanceStage_Implementation_AllTasksCompleted_Advances()
    {
        await SeedSprintAsync(stage: "Implementation");
        SeedTask("sprint-1", "Completed", "Task A");
        SeedTask("sprint-1", "Completed", "Task B");

        var sprint = await _service.AdvanceStageAsync("sprint-1");

        Assert.Equal("FinalSynthesis", sprint.CurrentStage);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_AllTasksCancelled_Advances()
    {
        await SeedSprintAsync(stage: "Implementation");
        SeedTask("sprint-1", "Cancelled", "Task A");
        SeedTask("sprint-1", "Cancelled", "Task B");

        var sprint = await _service.AdvanceStageAsync("sprint-1");

        Assert.Equal("FinalSynthesis", sprint.CurrentStage);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_MixedTerminalStatuses_Advances()
    {
        await SeedSprintAsync(stage: "Implementation");
        SeedTask("sprint-1", "Completed", "Task A");
        SeedTask("sprint-1", "Cancelled", "Task B");

        var sprint = await _service.AdvanceStageAsync("sprint-1");

        Assert.Equal("FinalSynthesis", sprint.CurrentStage);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_NoTasks_Advances()
    {
        await SeedSprintAsync(stage: "Implementation");

        var sprint = await _service.AdvanceStageAsync("sprint-1");

        Assert.Equal("FinalSynthesis", sprint.CurrentStage);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_ActiveTask_Throws()
    {
        await SeedSprintAsync(stage: "Implementation");
        SeedTask("sprint-1", "Active", "Unfinished task");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));

        Assert.Contains("Cannot advance from Implementation", ex.Message);
        Assert.Contains("1 task(s)", ex.Message);
        Assert.Contains("force=true", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_QueuedTask_Throws()
    {
        await SeedSprintAsync(stage: "Implementation");
        SeedTask("sprint-1", "Queued", "Queued task");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));

        Assert.Contains("Cannot advance from Implementation", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_InReviewTask_Throws()
    {
        await SeedSprintAsync(stage: "Implementation");
        SeedTask("sprint-1", "InReview", "Reviewing task");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));

        Assert.Contains("Cannot advance from Implementation", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_MultipleIncompleteTasks_ReportsCount()
    {
        await SeedSprintAsync(stage: "Implementation");
        SeedTask("sprint-1", "Active", "Task A");
        SeedTask("sprint-1", "InReview", "Task B");
        SeedTask("sprint-1", "Completed", "Task C");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1"));

        Assert.Contains("2 task(s)", ex.Message);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_Force_SkipsPrerequisites()
    {
        await SeedSprintAsync(stage: "Implementation");
        SeedTask("sprint-1", "Active", "Unfinished task");

        var sprint = await _service.AdvanceStageAsync("sprint-1", force: true);

        Assert.Equal("FinalSynthesis", sprint.CurrentStage);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_Force_IncludesForcedInEvent()
    {
        await SeedSprintAsync(stage: "Implementation");
        SeedTask("sprint-1", "Active", "Unfinished task");

        await _service.AdvanceStageAsync("sprint-1", force: true);

        var evt = await _db.ActivityEvents
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(evt);
        Assert.Contains("forced", evt.Message);
    }

    [Fact]
    public async Task AdvanceStage_NonImplementationStage_NoPrerequisiteCheck()
    {
        // Discussion stage has no artifact gate and no prerequisite — should advance freely
        await SeedSprintAsync(stage: "Discussion");
        SeedTask("sprint-1", "Active", "Active task during discussion");

        var sprint = await _service.AdvanceStageAsync("sprint-1");

        Assert.Equal("Validation", sprint.CurrentStage);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_TasksFromOtherSprint_Ignored()
    {
        await SeedSprintAsync(stage: "Implementation");
        await SeedSprintAsync(id: "sprint-other", number: 2, stage: "Planning");
        SeedTask("sprint-1", "Completed", "Our task");
        SeedTask("sprint-other", "Active", "Someone else's task");

        var sprint = await _service.AdvanceStageAsync("sprint-1");

        Assert.Equal("FinalSynthesis", sprint.CurrentStage);
    }

    [Fact]
    public async Task AdvanceStage_Implementation_Force_DoesNotSkipArtifactGate()
    {
        // Implementation has no artifact gate in current config, but let's verify
        // force behavior on a stage WITH an artifact gate (Validation needs ValidationReport)
        await SeedSprintAsync(stage: "Validation");
        SeedTask("sprint-1", "Active", "Task");

        // Should fail on artifact gate even with force=true
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync("sprint-1", force: true));

        Assert.Contains("ValidationReport", ex.Message);
    }
}
