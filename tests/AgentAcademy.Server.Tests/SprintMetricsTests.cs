using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for sprint metrics aggregation — per-sprint metrics and workspace-level summary.
/// </summary>
public class SprintMetricsTests : IDisposable
{
    private const string TestWorkspace = "/tmp/test-workspace";
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SprintService _service;
    private readonly SprintStageService _stageService;
    private readonly SprintArtifactService _artifactService;
    private readonly SprintMetricsCalculator _calculator;

    public SprintMetricsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _service = new SprintService(_db, new ActivityBroadcaster(), NullLogger<SprintService>.Instance);
        _stageService = new SprintStageService(_db, new ActivityBroadcaster(), NullLogger<SprintStageService>.Instance);
        _artifactService = new SprintArtifactService(_db, new ActivityBroadcaster(), NullLogger<SprintArtifactService>.Instance);
        _calculator = new SprintMetricsCalculator(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── GetSprintMetricsAsync ────────────────────────────────────

    [Fact]
    public async Task GetSprintMetrics_NotFound_ReturnsNull()
    {
        var result = await _calculator.GetSprintMetricsAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSprintMetrics_NewSprint_ReturnsBasicMetrics()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var metrics = await _calculator.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(sprint.Id, metrics.SprintId);
        Assert.Equal(1, metrics.SprintNumber);
        Assert.Equal(SprintStatus.Active, metrics.Status);
        Assert.Null(metrics.DurationSeconds); // active sprint has no duration
        Assert.Equal(0, metrics.TaskCount);
        Assert.Equal(0, metrics.CompletedTaskCount);
    }

    [Fact]
    public async Task GetSprintMetrics_CompletedSprint_HasDuration()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(sprint.Id, force: true);

        var metrics = await _calculator.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(SprintStatus.Completed, metrics.Status);
        Assert.NotNull(metrics.DurationSeconds);
        Assert.True(metrics.DurationSeconds >= 0);
    }

    [Fact]
    public async Task GetSprintMetrics_CancelledSprint_HasDuration()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CancelSprintAsync(sprint.Id);

        var metrics = await _calculator.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(SprintStatus.Cancelled, metrics.Status);
        // Cancelled sprints also get CompletedAt set
        Assert.NotNull(metrics.DurationSeconds);
    }

    [Fact]
    public async Task GetSprintMetrics_CountsArtifacts()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument",
            """{"Title":"T","Description":"D","InScope":[],"OutOfScope":[],"AcceptanceCriteria":[]}""");
        await _artifactService.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan",
            """{"Summary":"S","Phases":[]}""");

        var metrics = await _calculator.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(2, metrics.ArtifactCount);
    }

    [Fact]
    public async Task GetSprintMetrics_CountsTasks()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Add tasks linked to this sprint
        _db.Tasks.Add(new TaskEntity
        {
            Id = "task-1", Title = "Task 1", SprintId = sprint.Id,
            Status = "Active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.Tasks.Add(new TaskEntity
        {
            Id = "task-2", Title = "Task 2", SprintId = sprint.Id,
            Status = "Completed", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.Tasks.Add(new TaskEntity
        {
            Id = "task-3", Title = "Unrelated", SprintId = null,
            Status = "Active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var metrics = await _calculator.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(2, metrics.TaskCount);
        Assert.Equal(1, metrics.CompletedTaskCount);
    }

    [Fact]
    public async Task GetSprintMetrics_CountsStageTransitions()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Store required artifact then advance
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument",
            """{"Title":"T","Description":"D","InScope":[],"OutOfScope":[],"AcceptanceCriteria":[]}""");
        // Intake requires sign-off, so approve it
        await _stageService.AdvanceStageAsync(sprint.Id);
        await _stageService.ApproveAdvanceAsync(sprint.Id);

        // Now at Planning — store artifact and advance
        await _artifactService.StoreArtifactAsync(sprint.Id, "Planning", "SprintPlan",
            """{"Summary":"S","Phases":[]}""");
        await _stageService.AdvanceStageAsync(sprint.Id);
        await _stageService.ApproveAdvanceAsync(sprint.Id);

        var metrics = await _calculator.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        // Two actual transitions: Intake→Planning, Planning→Discussion
        Assert.True(metrics.StageTransitions >= 2,
            $"Expected at least 2 transitions, got {metrics.StageTransitions}");
    }

    [Fact]
    public async Task GetSprintMetrics_ComputesTimePerStage()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        // Store artifact and advance from Intake through sign-off
        await _artifactService.StoreArtifactAsync(sprint.Id, "Intake", "RequirementsDocument",
            """{"Title":"T","Description":"D","InScope":[],"OutOfScope":[],"AcceptanceCriteria":[]}""");
        await _stageService.AdvanceStageAsync(sprint.Id);
        await _stageService.ApproveAdvanceAsync(sprint.Id);

        var metrics = await _calculator.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        // Should have timing for at least Intake (completed stage) and Planning (current)
        Assert.True(metrics.TimePerStageSeconds.Count >= 1,
            $"Expected at least 1 stage timing, got {metrics.TimePerStageSeconds.Count}");
        Assert.True(metrics.TimePerStageSeconds.ContainsKey("Intake"),
            "Expected timing for Intake stage");
    }

    [Fact]
    public async Task GetSprintMetrics_ActiveSprint_IncludesCurrentStageTime()
    {
        var sprint = await _service.CreateSprintAsync(TestWorkspace);

        var metrics = await _calculator.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        // Active sprint should have time for the current stage (Intake)
        Assert.True(metrics.TimePerStageSeconds.ContainsKey("Intake"),
            "Expected timing for current Intake stage of active sprint");
        Assert.True(metrics.TimePerStageSeconds["Intake"] >= 0);
    }

    // ── GetMetricsSummaryAsync ───────────────────────────────────

    [Fact]
    public async Task GetMetricsSummary_NoSprints_ReturnsZeros()
    {
        var summary = await _calculator.GetMetricsSummaryAsync(TestWorkspace);

        Assert.Equal(0, summary.TotalSprints);
        Assert.Equal(0, summary.CompletedSprints);
        Assert.Equal(0, summary.CancelledSprints);
        Assert.Equal(0, summary.ActiveSprints);
        Assert.Null(summary.AverageDurationSeconds);
        Assert.Equal(0, summary.AverageTaskCount);
        Assert.Equal(0, summary.AverageArtifactCount);
        Assert.Empty(summary.AverageTimePerStageSeconds);
    }

    [Fact]
    public async Task GetMetricsSummary_CountsSprintsByStatus()
    {
        // Create 3 sprints: 1 completed, 1 cancelled, 1 active
        var s1 = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(s1.Id, force: true);

        var s2 = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CancelSprintAsync(s2.Id);

        var s3 = await _service.CreateSprintAsync(TestWorkspace);

        var summary = await _calculator.GetMetricsSummaryAsync(TestWorkspace);

        Assert.Equal(3, summary.TotalSprints);
        Assert.Equal(1, summary.CompletedSprints);
        Assert.Equal(1, summary.CancelledSprints);
        Assert.Equal(1, summary.ActiveSprints);
    }

    [Fact]
    public async Task GetMetricsSummary_AverageDuration_OnlyCompletedSprints()
    {
        var s1 = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(s1.Id, force: true);

        // Active sprint should not affect average duration
        var s2 = await _service.CreateSprintAsync(TestWorkspace);

        var summary = await _calculator.GetMetricsSummaryAsync(TestWorkspace);

        Assert.NotNull(summary.AverageDurationSeconds);
        Assert.True(summary.AverageDurationSeconds >= 0);
    }

    [Fact]
    public async Task GetMetricsSummary_AveragesArtifactsAndTasks()
    {
        var s1 = await _service.CreateSprintAsync(TestWorkspace);
        await _artifactService.StoreArtifactAsync(s1.Id, "Intake", "RequirementsDocument",
            """{"Title":"T","Description":"D","InScope":[],"OutOfScope":[],"AcceptanceCriteria":[]}""");

        _db.Tasks.Add(new TaskEntity
        {
            Id = "t1", Title = "Task 1", SprintId = s1.Id,
            Status = "Active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.Tasks.Add(new TaskEntity
        {
            Id = "t2", Title = "Task 2", SprintId = s1.Id,
            Status = "Active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await _service.CompleteSprintAsync(s1.Id, force: true);

        var s2 = await _service.CreateSprintAsync(TestWorkspace);

        var summary = await _calculator.GetMetricsSummaryAsync(TestWorkspace);

        // 2 sprints, 1 artifact total → avg 0.5
        Assert.Equal(0.5, summary.AverageArtifactCount);
        // 2 sprints, 2 tasks total → avg 1.0
        Assert.Equal(1.0, summary.AverageTaskCount);
    }

    [Fact]
    public async Task GetMetricsSummary_ScopedToWorkspace()
    {
        var s1 = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(s1.Id, force: true);

        // Sprint in a different workspace
        var s2 = await _service.CreateSprintAsync("/tmp/other-workspace");

        var summary = await _calculator.GetMetricsSummaryAsync(TestWorkspace);

        Assert.Equal(1, summary.TotalSprints);
        Assert.Equal(1, summary.CompletedSprints);
    }

    [Fact]
    public async Task GetMetricsSummary_AverageTimePerStage_AcrossSprints()
    {
        var s1 = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(s1.Id, force: true);

        var s2 = await _service.CreateSprintAsync(TestWorkspace);
        await _service.CompleteSprintAsync(s2.Id, force: true);

        var summary = await _calculator.GetMetricsSummaryAsync(TestWorkspace);

        // Both sprints spent time in at least Intake (initial stage)
        Assert.True(summary.AverageTimePerStageSeconds.Count >= 1,
            "Expected at least 1 stage in average timing");
    }
}
