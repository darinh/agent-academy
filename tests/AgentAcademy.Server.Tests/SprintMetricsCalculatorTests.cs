using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="SprintMetricsCalculator"/> — per-sprint metrics
/// and workspace-level summary computations using direct entity insertion
/// (no dependency on SprintService lifecycle methods).
/// </summary>
public sealed class SprintMetricsCalculatorTests : IDisposable
{
    private const string Workspace = "/test/workspace";
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SprintMetricsCalculator _sut;

    public SprintMetricsCalculatorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new SprintMetricsCalculator(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private int _nextNumber;

    private SprintEntity AddSprint(
        string? id = null,
        int? number = null,
        string status = "Active",
        string currentStage = "Intake",
        DateTime? createdAt = null,
        DateTime? completedAt = null,
        string? workspace = null)
    {
        var sprint = new SprintEntity
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Number = number ?? ++_nextNumber,
            WorkspacePath = workspace ?? Workspace,
            Status = status,
            CurrentStage = currentStage,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            CompletedAt = completedAt,
        };
        _db.Sprints.Add(sprint);
        _db.SaveChanges();
        return sprint;
    }

    private void AddEvent(string sprintId, string type, DateTime occurredAt,
        Dictionary<string, object?>? extraMeta = null)
    {
        var meta = new Dictionary<string, object?> { ["sprintId"] = sprintId };
        if (extraMeta is not null)
        {
            foreach (var (k, v) in extraMeta)
                meta[k] = v;
        }

        _db.ActivityEvents.Add(new ActivityEventEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            Severity = "Info",
            Message = type,
            OccurredAt = occurredAt,
            MetadataJson = JsonSerializer.Serialize(meta),
        });
        _db.SaveChanges();
    }

    private void AddStageAdvanceEvent(string sprintId, DateTime occurredAt,
        string previousStage, string currentStage, string? action = null)
    {
        var meta = new Dictionary<string, object?>
        {
            ["sprintId"] = sprintId,
            ["previousStage"] = previousStage,
            ["currentStage"] = currentStage,
        };
        if (action is not null)
            meta["action"] = action;

        AddEvent(sprintId, nameof(ActivityEventType.SprintStageAdvanced), occurredAt, meta);
    }

    private void AddArtifact(string sprintId, string stage = "Intake", string type = "RequirementsDocument")
    {
        _db.SprintArtifacts.Add(new SprintArtifactEntity
        {
            SprintId = sprintId,
            Stage = stage,
            Type = type,
            Content = "{}",
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private void AddTask(string sprintId, string status = "Active")
    {
        _db.Tasks.Add(new TaskEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "Task",
            SprintId = sprintId,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    // ═══════════════════════════════════════════════════════════════
    // GetSprintMetricsAsync — Sprint Not Found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSprintMetrics_NonexistentSprint_ReturnsNull()
    {
        var result = await _sut.GetSprintMetricsAsync("does-not-exist");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSprintMetrics_EmptyId_ReturnsNull()
    {
        var result = await _sut.GetSprintMetricsAsync("");
        Assert.Null(result);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetSprintMetricsAsync — Basic Sprint Properties
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSprintMetrics_ActiveSprint_ReturnsActiveStatus()
    {
        var sprint = AddSprint(status: "Active");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(SprintStatus.Active, metrics.Status);
    }

    [Fact]
    public async Task GetSprintMetrics_PreservesSprintNumber()
    {
        var sprint = AddSprint(number: 42);

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(42, metrics.SprintNumber);
    }

    [Fact]
    public async Task GetSprintMetrics_PreservesSprintId()
    {
        var sprint = AddSprint(id: "test-sprint-id");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal("test-sprint-id", metrics.SprintId);
    }

    [Fact]
    public async Task GetSprintMetrics_PreservesCreatedAt()
    {
        var created = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var sprint = AddSprint(createdAt: created);

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(created, metrics.CreatedAt);
    }

    [Fact]
    public async Task GetSprintMetrics_ActiveSprint_CompletedAtIsNull()
    {
        var sprint = AddSprint(status: "Active");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Null(metrics.CompletedAt);
    }

    [Fact]
    public async Task GetSprintMetrics_CompletedSprint_HasCompletedAt()
    {
        var completed = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var sprint = AddSprint(status: "Completed", completedAt: completed);

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(completed, metrics.CompletedAt);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetSprintMetricsAsync — Duration
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSprintMetrics_ActiveSprint_DurationIsNull()
    {
        var sprint = AddSprint(status: "Active");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Null(metrics.DurationSeconds);
    }

    [Fact]
    public async Task GetSprintMetrics_CompletedSprint_DurationIsCorrect()
    {
        var created = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var completed = new DateTime(2024, 6, 15, 11, 30, 0, DateTimeKind.Utc);
        var sprint = AddSprint(status: "Completed", createdAt: created, completedAt: completed);

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(5400.0, metrics.DurationSeconds); // 90 minutes
    }

    [Fact]
    public async Task GetSprintMetrics_CancelledSprint_HasDuration()
    {
        var created = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var completed = new DateTime(2024, 6, 15, 10, 15, 0, DateTimeKind.Utc);
        var sprint = AddSprint(status: "Cancelled", createdAt: created, completedAt: completed);

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(SprintStatus.Cancelled, metrics.Status);
        Assert.NotNull(metrics.DurationSeconds);
        Assert.Equal(900.0, metrics.DurationSeconds); // 15 minutes
    }

    // ═══════════════════════════════════════════════════════════════
    // GetSprintMetricsAsync — Artifact Count
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSprintMetrics_NoArtifacts_CountIsZero()
    {
        var sprint = AddSprint();

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.ArtifactCount);
    }

    [Fact]
    public async Task GetSprintMetrics_MultipleArtifacts_CountIsCorrect()
    {
        var sprint = AddSprint();
        AddArtifact(sprint.Id, "Intake", "RequirementsDocument");
        AddArtifact(sprint.Id, "Planning", "SprintPlan");
        AddArtifact(sprint.Id, "Validation", "ValidationReport");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(3, metrics.ArtifactCount);
    }

    [Fact]
    public async Task GetSprintMetrics_ArtifactsFromOtherSprint_NotCounted()
    {
        var sprint1 = AddSprint();
        var sprint2 = AddSprint(workspace: "/other");
        AddArtifact(sprint1.Id);
        AddArtifact(sprint2.Id);

        var metrics = await _sut.GetSprintMetricsAsync(sprint1.Id);

        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.ArtifactCount);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetSprintMetricsAsync — Task Count
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSprintMetrics_NoTasks_CountIsZero()
    {
        var sprint = AddSprint();

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.TaskCount);
        Assert.Equal(0, metrics.CompletedTaskCount);
    }

    [Fact]
    public async Task GetSprintMetrics_MixedTaskStatuses_CountsCorrectly()
    {
        var sprint = AddSprint();
        AddTask(sprint.Id, "Active");
        AddTask(sprint.Id, "Completed");
        AddTask(sprint.Id, "Completed");
        AddTask(sprint.Id, "Blocked");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(4, metrics.TaskCount);
        Assert.Equal(2, metrics.CompletedTaskCount);
    }

    [Fact]
    public async Task GetSprintMetrics_TasksFromOtherSprint_NotCounted()
    {
        var sprint1 = AddSprint();
        var sprint2 = AddSprint(workspace: "/other");
        AddTask(sprint1.Id, "Active");
        AddTask(sprint2.Id, "Completed");

        var metrics = await _sut.GetSprintMetricsAsync(sprint1.Id);

        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.TaskCount);
        Assert.Equal(0, metrics.CompletedTaskCount);
    }

    [Fact]
    public async Task GetSprintMetrics_UnassignedTasks_NotCounted()
    {
        var sprint = AddSprint();
        AddTask(sprint.Id, "Active");

        // Task with no sprint
        _db.Tasks.Add(new TaskEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "Unlinked",
            SprintId = null,
            Status = "Completed",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.TaskCount);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetSprintMetricsAsync — Stage Transitions
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSprintMetrics_NoEvents_ZeroTransitions()
    {
        var sprint = AddSprint();

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.StageTransitions);
    }

    [Fact]
    public async Task GetSprintMetrics_SingleStageAdvance_OneTransition()
    {
        var sprint = AddSprint();
        var t0 = sprint.CreatedAt;

        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(5), "Intake", "Planning");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.StageTransitions);
    }

    [Fact]
    public async Task GetSprintMetrics_MultipleStageAdvances_CountsAll()
    {
        var sprint = AddSprint();
        var t0 = sprint.CreatedAt;

        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(5), "Intake", "Planning");
        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(10), "Planning", "Discussion");
        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(15), "Discussion", "Validation");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(3, metrics.StageTransitions);
    }

    [Fact]
    public async Task GetSprintMetrics_SignoffRequestedEvents_ExcludedFromTransitions()
    {
        var sprint = AddSprint();
        var t0 = sprint.CreatedAt;

        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(5), "Intake", "Planning");
        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(8), "Planning", "Discussion",
            action: "signoff_requested");
        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(10), "Planning", "Discussion");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(2, metrics.StageTransitions); // signoff_requested excluded
    }

    [Fact]
    public async Task GetSprintMetrics_OnlySignoffRequested_ZeroTransitions()
    {
        var sprint = AddSprint();
        var t0 = sprint.CreatedAt;

        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(5), "Intake", "Planning",
            action: "signoff_requested");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.StageTransitions);
    }

    [Fact]
    public async Task GetSprintMetrics_SprintStartedEvent_NotCountedAsTransition()
    {
        var sprint = AddSprint();
        var t0 = sprint.CreatedAt;

        AddEvent(sprint.Id, nameof(ActivityEventType.SprintStarted), t0);

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.StageTransitions);
    }

    [Fact]
    public async Task GetSprintMetrics_EventsFromOtherSprint_NotCounted()
    {
        var sprint1 = AddSprint();
        var sprint2 = AddSprint(workspace: "/other");
        var t0 = sprint1.CreatedAt;

        AddStageAdvanceEvent(sprint2.Id, t0.AddMinutes(5), "Intake", "Planning");

        var metrics = await _sut.GetSprintMetricsAsync(sprint1.Id);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.StageTransitions);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetSprintMetricsAsync — Time Per Stage
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TimePerStage_NoEvents_EntireTimeInCurrentStage()
    {
        var created = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var completed = new DateTime(2024, 6, 15, 11, 0, 0, DateTimeKind.Utc);
        var sprint = AddSprint(
            status: "Completed", currentStage: "Intake",
            createdAt: created, completedAt: completed);

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        // No events → fallback: entire duration in CurrentStage
        Assert.Single(metrics.TimePerStageSeconds);
        Assert.True(metrics.TimePerStageSeconds.ContainsKey("Intake"));
        Assert.Equal(3600.0, metrics.TimePerStageSeconds["Intake"]);
    }

    [Fact]
    public async Task TimePerStage_SingleAdvance_SplitsBetweenStages()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var sprint = AddSprint(
            status: "Completed", currentStage: "Planning",
            createdAt: t0, completedAt: t0.AddMinutes(30));

        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(10), "Intake", "Planning");
        AddEvent(sprint.Id, nameof(ActivityEventType.SprintCompleted), t0.AddMinutes(30));

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(600.0, metrics.TimePerStageSeconds["Intake"]); // 10 min
        Assert.Equal(1200.0, metrics.TimePerStageSeconds["Planning"]); // 20 min
    }

    [Fact]
    public async Task TimePerStage_MultipleAdvances_TracksEachStage()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var sprint = AddSprint(
            status: "Completed", currentStage: "Discussion",
            createdAt: t0, completedAt: t0.AddMinutes(45));

        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(10), "Intake", "Planning");
        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(25), "Planning", "Discussion");
        AddEvent(sprint.Id, nameof(ActivityEventType.SprintCompleted), t0.AddMinutes(45));

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(600.0, metrics.TimePerStageSeconds["Intake"]);     // 10 min
        Assert.Equal(900.0, metrics.TimePerStageSeconds["Planning"]);   // 15 min
        Assert.Equal(1200.0, metrics.TimePerStageSeconds["Discussion"]); // 20 min
    }

    [Fact]
    public async Task TimePerStage_SprintStartedEvent_Skipped()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var sprint = AddSprint(
            status: "Completed", currentStage: "Intake",
            createdAt: t0, completedAt: t0.AddMinutes(20));

        AddEvent(sprint.Id, nameof(ActivityEventType.SprintStarted), t0);
        AddEvent(sprint.Id, nameof(ActivityEventType.SprintCompleted), t0.AddMinutes(20));

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(1200.0, metrics.TimePerStageSeconds["Intake"]); // 20 min
    }

    [Fact]
    public async Task TimePerStage_SignoffRequested_SkippedInTiming()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var sprint = AddSprint(
            status: "Completed", currentStage: "Planning",
            createdAt: t0, completedAt: t0.AddMinutes(30));

        // signoff_requested should not split stage timing
        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(8), "Intake", "Planning",
            action: "signoff_requested");
        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(10), "Intake", "Planning");
        AddEvent(sprint.Id, nameof(ActivityEventType.SprintCompleted), t0.AddMinutes(30));

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        // signoff_requested skipped → Intake runs from t0 to t0+10m
        Assert.Equal(600.0, metrics.TimePerStageSeconds["Intake"]);
        Assert.Equal(1200.0, metrics.TimePerStageSeconds["Planning"]);
    }

    [Fact]
    public async Task TimePerStage_CancelledSprint_RecordsFinalStageTime()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var sprint = AddSprint(
            status: "Cancelled", currentStage: "Planning",
            createdAt: t0, completedAt: t0.AddMinutes(20));

        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(5), "Intake", "Planning");
        AddEvent(sprint.Id, nameof(ActivityEventType.SprintCancelled), t0.AddMinutes(20));

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.Equal(300.0, metrics.TimePerStageSeconds["Intake"]);    // 5 min
        Assert.Equal(900.0, metrics.TimePerStageSeconds["Planning"]);  // 15 min
    }

    [Fact]
    public async Task TimePerStage_ActiveSprint_IncludesCurrentStageAccrual()
    {
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        var sprint = AddSprint(
            status: "Active", currentStage: "Planning", createdAt: t0);

        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(10), "Intake", "Planning");

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        Assert.NotNull(metrics);
        Assert.True(metrics.TimePerStageSeconds.ContainsKey("Intake"));
        Assert.True(metrics.TimePerStageSeconds.ContainsKey("Planning"));
        // Planning time should be ~20 min (active accrual)
        Assert.True(metrics.TimePerStageSeconds["Planning"] >= 1100,
            $"Expected Planning ≥ 1100s, got {metrics.TimePerStageSeconds["Planning"]}");
    }

    [Fact]
    public async Task TimePerStage_NullMetadataJson_TreatedAsEmpty()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var sprint = AddSprint(
            status: "Completed", currentStage: "Intake",
            createdAt: t0, completedAt: t0.AddMinutes(10));

        // Event with null MetadataJson won't match any sprint
        _db.ActivityEvents.Add(new ActivityEventEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = nameof(ActivityEventType.SprintStageAdvanced),
            Severity = "Info",
            Message = "advance",
            OccurredAt = t0.AddMinutes(5),
            MetadataJson = null,
        });
        _db.SaveChanges();

        var metrics = await _sut.GetSprintMetricsAsync(sprint.Id);

        // Null metadata → event doesn't belong to this sprint → no events for sprint
        Assert.NotNull(metrics);
        Assert.Single(metrics.TimePerStageSeconds);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetMetricsSummaryAsync — No Sprints
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetMetricsSummary_NoSprints_ReturnsAllZeros()
    {
        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

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
    public async Task GetMetricsSummary_DifferentWorkspace_NotIncluded()
    {
        AddSprint(workspace: "/other/workspace");

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.Equal(0, summary.TotalSprints);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetMetricsSummaryAsync — Status Counts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetMetricsSummary_SingleActiveSprint()
    {
        AddSprint(status: "Active");

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.Equal(1, summary.TotalSprints);
        Assert.Equal(0, summary.CompletedSprints);
        Assert.Equal(0, summary.CancelledSprints);
        Assert.Equal(1, summary.ActiveSprints);
    }

    [Fact]
    public async Task GetMetricsSummary_MixedStatuses_CountsCorrectly()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        AddSprint(number: 1, status: "Completed", createdAt: t0, completedAt: t0.AddHours(1));
        AddSprint(number: 2, status: "Completed", createdAt: t0, completedAt: t0.AddHours(2));
        AddSprint(number: 3, status: "Cancelled", createdAt: t0, completedAt: t0.AddMinutes(30));
        AddSprint(number: 4, status: "Active", createdAt: t0);

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.Equal(4, summary.TotalSprints);
        Assert.Equal(2, summary.CompletedSprints);
        Assert.Equal(1, summary.CancelledSprints);
        Assert.Equal(1, summary.ActiveSprints);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetMetricsSummaryAsync — Average Duration
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetMetricsSummary_NoCompletedSprints_AverageDurationIsNull()
    {
        AddSprint(status: "Active");
        AddSprint(number: 2, status: "Cancelled",
            createdAt: DateTime.UtcNow.AddHours(-1), completedAt: DateTime.UtcNow);

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        // Cancelled sprints don't contribute to average duration
        Assert.Null(summary.AverageDurationSeconds);
    }

    [Fact]
    public async Task GetMetricsSummary_CompletedSprints_AverageDurationCorrect()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        // Sprint 1: 1 hour
        AddSprint(number: 1, status: "Completed", createdAt: t0, completedAt: t0.AddHours(1));
        // Sprint 2: 3 hours
        AddSprint(number: 2, status: "Completed", createdAt: t0, completedAt: t0.AddHours(3));

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.NotNull(summary.AverageDurationSeconds);
        Assert.Equal(7200.0, summary.AverageDurationSeconds); // avg of 3600 + 10800 = 7200
    }

    [Fact]
    public async Task GetMetricsSummary_ActiveSprintsExcludedFromDurationAverage()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        AddSprint(number: 1, status: "Completed", createdAt: t0, completedAt: t0.AddHours(2));
        AddSprint(number: 2, status: "Active", createdAt: t0); // should not affect avg

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.NotNull(summary.AverageDurationSeconds);
        Assert.Equal(7200.0, summary.AverageDurationSeconds); // only the completed sprint
    }

    // ═══════════════════════════════════════════════════════════════
    // GetMetricsSummaryAsync — Average Task/Artifact Counts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetMetricsSummary_AverageArtifactCount()
    {
        var s1 = AddSprint(status: "Completed",
            createdAt: DateTime.UtcNow.AddHours(-1), completedAt: DateTime.UtcNow);
        var s2 = AddSprint();

        AddArtifact(s1.Id, "Intake", "RequirementsDocument");
        AddArtifact(s1.Id, "Planning", "SprintPlan");
        AddArtifact(s1.Id, "Validation", "ValidationReport");
        // s2 has no artifacts

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.Equal(1.5, summary.AverageArtifactCount); // 3 artifacts / 2 sprints
    }

    [Fact]
    public async Task GetMetricsSummary_AverageTaskCount()
    {
        var s1 = AddSprint(status: "Completed",
            createdAt: DateTime.UtcNow.AddHours(-1), completedAt: DateTime.UtcNow);
        var s2 = AddSprint();

        AddTask(s1.Id, "Active");
        AddTask(s1.Id, "Completed");
        AddTask(s2.Id, "Active");

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.Equal(1.5, summary.AverageTaskCount); // 3 tasks / 2 sprints
    }

    [Fact]
    public async Task GetMetricsSummary_NoArtifactsOrTasks_AveragesAreZero()
    {
        AddSprint();

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.Equal(0.0, summary.AverageArtifactCount);
        Assert.Equal(0.0, summary.AverageTaskCount);
    }

    // ═══════════════════════════════════════════════════════════════
    // GetMetricsSummaryAsync — Average Time Per Stage
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetMetricsSummary_AverageTimePerStage_SingleSprint()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var sprint = AddSprint(
            status: "Completed", currentStage: "Planning",
            createdAt: t0, completedAt: t0.AddMinutes(30));

        AddStageAdvanceEvent(sprint.Id, t0.AddMinutes(10), "Intake", "Planning");
        AddEvent(sprint.Id, nameof(ActivityEventType.SprintCompleted), t0.AddMinutes(30));

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.Equal(600.0, summary.AverageTimePerStageSeconds["Intake"]);
        Assert.Equal(1200.0, summary.AverageTimePerStageSeconds["Planning"]);
    }

    [Fact]
    public async Task GetMetricsSummary_AverageTimePerStage_AcrossMultipleSprints()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        // Sprint 1: Intake = 10 min
        var s1 = AddSprint(
            number: 1, status: "Completed", currentStage: "Planning",
            createdAt: t0, completedAt: t0.AddMinutes(20));
        AddStageAdvanceEvent(s1.Id, t0.AddMinutes(10), "Intake", "Planning");
        AddEvent(s1.Id, nameof(ActivityEventType.SprintCompleted), t0.AddMinutes(20));

        // Sprint 2: Intake = 20 min
        var s2 = AddSprint(
            number: 2, status: "Completed", currentStage: "Planning",
            createdAt: t0, completedAt: t0.AddMinutes(40));
        AddStageAdvanceEvent(s2.Id, t0.AddMinutes(20), "Intake", "Planning");
        AddEvent(s2.Id, nameof(ActivityEventType.SprintCompleted), t0.AddMinutes(40));

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        // Average Intake: (600 + 1200) / 2 = 900
        Assert.Equal(900.0, summary.AverageTimePerStageSeconds["Intake"]);
    }

    [Fact]
    public async Task GetMetricsSummary_SprintWithNoEvents_StillContributes()
    {
        var t0 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        // Sprint with no events → all time in CurrentStage
        AddSprint(
            status: "Completed", currentStage: "Intake",
            createdAt: t0, completedAt: t0.AddMinutes(60));

        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.True(summary.AverageTimePerStageSeconds.ContainsKey("Intake"));
        Assert.Equal(3600.0, summary.AverageTimePerStageSeconds["Intake"]);
    }

    [Fact]
    public async Task GetMetricsSummary_EmptyWorkspace_EmptyStageTimings()
    {
        var summary = await _sut.GetMetricsSummaryAsync(Workspace);

        Assert.Empty(summary.AverageTimePerStageSeconds);
    }
}
