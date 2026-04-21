using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

public sealed class TaskAnalyticsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly TaskAnalyticsService _sut;

    private static readonly AgentCatalogOptions TestCatalog = new(
        DefaultRoomId: "main",
        DefaultRoomName: "Main Room",
        Agents: new List<AgentDefinition>
        {
            new("agent-1", "Hephaestus", "Developer", "Backend engineer", "", "gpt-4", new(), new(), true),
            new("agent-2", "Athena", "Developer", "Frontend engineer", "", "gpt-4", new(), new(), true),
        });

    public TaskAnalyticsServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _sut = new TaskAnalyticsService(_db, TestCatalog);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static int _taskCounter;

    private TaskEntity MakeTask(
        string? agentId = null,
        string status = "Active",
        string type = "Feature",
        int reviewRounds = 0,
        int commitCount = 0,
        DateTime? createdAt = null,
        DateTime? startedAt = null,
        DateTime? completedAt = null)
    {
        var id = $"T-{Interlocked.Increment(ref _taskCounter)}";
        var now = DateTime.UtcNow;
        return new TaskEntity
        {
            Id = id,
            Title = $"Task {id}",
            Description = "Test task",
            SuccessCriteria = "",
            Status = status,
            Type = type,
            CurrentPhase = "Planning",
            CurrentPlan = "",
            ValidationStatus = "NotStarted",
            ValidationSummary = "",
            ImplementationStatus = "NotStarted",
            ImplementationSummary = "",
            PreferredRoles = "[]",
            FleetModels = "[]",
            AssignedAgentId = agentId,
            AssignedAgentName = agentId,
            ReviewRounds = reviewRounds,
            CommitCount = commitCount,
            CreatedAt = createdAt ?? now,
            UpdatedAt = now,
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };
    }

    private async Task SeedAndClear(params TaskEntity[] tasks)
    {
        _db.Tasks.AddRange(tasks);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    // ── Empty state ──

    [Fact]
    public async Task EmptyDatabase_ReturnsZeroCountsAndNullAverages()
    {
        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(0, result.Overview.TotalTasks);
        Assert.Equal(0, result.Overview.StatusCounts.Completed);
        Assert.Equal(0.0, result.Overview.CompletionRate);
        Assert.Null(result.Overview.AvgCycleTimeHours);
        Assert.Null(result.Overview.AvgQueueTimeHours);
        Assert.Null(result.Overview.AvgExecutionSpanHours);
        Assert.Null(result.Overview.AvgReviewRounds);
        Assert.Equal(0.0, result.Overview.ReworkRate);
        Assert.Equal(0, result.Overview.TotalCommits);
        Assert.Empty(result.AgentEffectiveness);
        Assert.Equal(12, result.ThroughputBuckets.Count);
    }

    // ── Status counts ──

    [Fact]
    public async Task StatusCounts_ReflectsAllTasksRegardlessOfTimeWindow()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Active", createdAt: now.AddHours(-100)),
            MakeTask(status: "Completed", completedAt: now.AddHours(-100), createdAt: now.AddHours(-200)),
            MakeTask(status: "Blocked"),
            MakeTask(status: "Queued"));

        var result = await _sut.GetTaskCycleAnalyticsAsync(hoursBack: 24);

        Assert.Equal(1, result.Overview.StatusCounts.Active);
        Assert.Equal(1, result.Overview.StatusCounts.Completed);
        Assert.Equal(1, result.Overview.StatusCounts.Blocked);
        Assert.Equal(1, result.Overview.StatusCounts.Queued);
    }

    // ── Type breakdown ──

    [Fact]
    public async Task TypeBreakdown_CountsFeatureBugChoreSpikeCorrectly()
    {
        await SeedAndClear(
            MakeTask(type: "Feature"),
            MakeTask(type: "Feature"),
            MakeTask(type: "Bug"),
            MakeTask(type: "Chore"),
            MakeTask(type: "Spike"),
            MakeTask(type: "Spike"));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(2, result.TypeBreakdown.Feature);
        Assert.Equal(1, result.TypeBreakdown.Bug);
        Assert.Equal(1, result.TypeBreakdown.Chore);
        Assert.Equal(2, result.TypeBreakdown.Spike);
    }

    // ── Completion rate ──

    [Fact]
    public async Task CompletionRate_CalculatedAsCompletedOverTotal()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Completed", completedAt: now),
            MakeTask(status: "Completed", completedAt: now),
            MakeTask(status: "Active"),
            MakeTask(status: "Cancelled"));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(4, result.Overview.TotalTasks);
        Assert.Equal(0.5, result.Overview.CompletionRate);
    }

    [Fact]
    public async Task CompletionRate_UsesUnionOfCreatedAndCompletedIds()
    {
        var now = DateTime.UtcNow;
        // Task created outside window but completed inside — still counted in denominator
        await SeedAndClear(
            MakeTask(status: "Completed", createdAt: now.AddHours(-48), completedAt: now.AddHours(-2)),
            MakeTask(status: "Completed", createdAt: now.AddHours(-10), completedAt: now.AddHours(-1)));

        var result = await _sut.GetTaskCycleAnalyticsAsync(hoursBack: 24);

        Assert.True(result.Overview.CompletionRate <= 1.0,
            $"Completion rate {result.Overview.CompletionRate} should not exceed 1.0");
        Assert.Equal(2, result.Overview.TotalTasks);
    }

    // ── Cycle time metrics ──

    [Fact]
    public async Task AvgCycleTime_ComputedFromCreatedAtToCompletedAt()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Completed", createdAt: now.AddHours(-10), completedAt: now),
            MakeTask(status: "Completed", createdAt: now.AddHours(-20), completedAt: now));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.NotNull(result.Overview.AvgCycleTimeHours);
        Assert.Equal(15.0, result.Overview.AvgCycleTimeHours!.Value, 1);
    }

    [Fact]
    public async Task AvgQueueTime_ComputedFromCreatedAtToStartedAt()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Completed", createdAt: now.AddHours(-10), startedAt: now.AddHours(-8), completedAt: now),
            MakeTask(status: "Completed", createdAt: now.AddHours(-20), startedAt: now.AddHours(-16), completedAt: now));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.NotNull(result.Overview.AvgQueueTimeHours);
        Assert.Equal(3.0, result.Overview.AvgQueueTimeHours!.Value, 1);
    }

    [Fact]
    public async Task AvgExecutionSpan_ComputedFromStartedAtToCompletedAt()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Completed", createdAt: now.AddHours(-10), startedAt: now.AddHours(-8), completedAt: now),
            MakeTask(status: "Completed", createdAt: now.AddHours(-20), startedAt: now.AddHours(-16), completedAt: now));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.NotNull(result.Overview.AvgExecutionSpanHours);
        Assert.Equal(12.0, result.Overview.AvgExecutionSpanHours!.Value, 1);
    }

    // ── Rework rate ──

    [Fact]
    public async Task ReworkRate_CountsTasksWithReviewRoundsGreaterThanOne()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Completed", completedAt: now, reviewRounds: 1),
            MakeTask(status: "Completed", completedAt: now, reviewRounds: 3),
            MakeTask(status: "Completed", completedAt: now, reviewRounds: 2),
            MakeTask(status: "Completed", completedAt: now, reviewRounds: 0));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(0.5, result.Overview.ReworkRate);
    }

    // ── Total commits ──

    [Fact]
    public async Task TotalCommits_SumsFromCompletedTasksOnly()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Completed", completedAt: now, commitCount: 5),
            MakeTask(status: "Completed", completedAt: now, commitCount: 3),
            MakeTask(status: "Active", commitCount: 10));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(8, result.Overview.TotalCommits);
    }

    // ── Per-agent effectiveness ──

    [Fact]
    public async Task AgentEffectiveness_CountsAssignedCompletedCancelled()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(agentId: "agent-1", status: "Completed", completedAt: now, commitCount: 3),
            MakeTask(agentId: "agent-1", status: "Cancelled"),
            MakeTask(agentId: "agent-1", status: "Active"),
            MakeTask(agentId: "agent-2", status: "Completed", completedAt: now));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        var a1 = result.AgentEffectiveness.First(a => a.AgentId == "agent-1");
        Assert.Equal(3, a1.Assigned);
        Assert.Equal(1, a1.Completed);
        Assert.Equal(1, a1.Cancelled);
    }

    [Fact]
    public async Task AgentCompletionRate_DoesNotExceedOne()
    {
        var now = DateTime.UtcNow;
        // Task created outside window but completed inside window by agent
        await SeedAndClear(
            MakeTask(agentId: "agent-1", status: "Completed", createdAt: now.AddHours(-48), completedAt: now.AddHours(-1)),
            MakeTask(agentId: "agent-1", status: "Completed", createdAt: now.AddHours(-5), completedAt: now.AddHours(-1)));

        var result = await _sut.GetTaskCycleAnalyticsAsync(hoursBack: 24);

        var a1 = result.AgentEffectiveness.First(a => a.AgentId == "agent-1");
        Assert.True(a1.CompletionRate <= 1.0,
            $"Agent completion rate {a1.CompletionRate} should not exceed 1.0");
    }

    [Fact]
    public async Task AgentFirstPassApprovalRate_ReviewRoundsLessThanOrEqualOne()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(agentId: "agent-1", status: "Completed", completedAt: now, reviewRounds: 1),
            MakeTask(agentId: "agent-1", status: "Completed", completedAt: now, reviewRounds: 3));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        var a1 = result.AgentEffectiveness.First(a => a.AgentId == "agent-1");
        Assert.Equal(0.5, a1.FirstPassApprovalRate);
    }

    // ── Throughput buckets ──

    [Fact]
    public async Task ThroughputBuckets_SpansTimeWindowWith12Buckets()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Completed", createdAt: now.AddHours(-5), completedAt: now));

        var result = await _sut.GetTaskCycleAnalyticsAsync(hoursBack: 24);

        Assert.Equal(12, result.ThroughputBuckets.Count);
        Assert.Equal(1, result.ThroughputBuckets.Sum(b => b.Completed));
    }

    // ── Time window filtering ──

    [Fact]
    public async Task HoursBackNull_ReturnsAllTasks()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Completed", createdAt: now.AddDays(-30), completedAt: now.AddDays(-29)),
            MakeTask(status: "Completed", createdAt: now.AddHours(-1), completedAt: now));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(2, result.Overview.TotalTasks);
    }

    [Fact]
    public async Task HoursBack_FiltersCompletedTasksByCompletedAt()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Completed", createdAt: now.AddHours(-72), completedAt: now.AddHours(-48)),
            MakeTask(status: "Completed", createdAt: now.AddHours(-10), completedAt: now.AddHours(-2)));

        var result = await _sut.GetTaskCycleAnalyticsAsync(hoursBack: 24);

        Assert.NotNull(result.Overview.AvgCycleTimeHours);
        Assert.Equal(8.0, result.Overview.AvgCycleTimeHours!.Value, 1);
    }

    [Fact]
    public async Task HoursBack_FiltersCreatedTasksByCreatedAt()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(status: "Active", type: "Feature", createdAt: now.AddHours(-48)),
            MakeTask(status: "Active", type: "Bug", createdAt: now.AddHours(-2)));

        var result = await _sut.GetTaskCycleAnalyticsAsync(hoursBack: 24);

        Assert.Equal(0, result.TypeBreakdown.Feature);
        Assert.Equal(1, result.TypeBreakdown.Bug);
    }

    // ── Null avg metrics ──

    [Fact]
    public async Task AvgMetrics_NullWhenNoCompletedTasks()
    {
        await SeedAndClear(
            MakeTask(status: "Active"),
            MakeTask(status: "Queued"));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.Null(result.Overview.AvgCycleTimeHours);
        Assert.Null(result.Overview.AvgQueueTimeHours);
        Assert.Null(result.Overview.AvgExecutionSpanHours);
        Assert.Null(result.Overview.AvgReviewRounds);
    }

    // ── Agent name lookup ──

    [Fact]
    public async Task AgentNameLookup_UsesCatalogMapping()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(agentId: "agent-1", status: "Completed", completedAt: now),
            MakeTask(agentId: "unknown-agent", status: "Completed", completedAt: now));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.Contains(result.AgentEffectiveness, a => a.AgentId == "agent-1" && a.AgentName == "Hephaestus");
        Assert.Contains(result.AgentEffectiveness, a => a.AgentId == "unknown-agent" && a.AgentName == "unknown-agent");
    }

    // ── Ordering ──

    [Fact]
    public async Task AgentEffectiveness_OrderedByCompletedDescending()
    {
        var now = DateTime.UtcNow;
        await SeedAndClear(
            MakeTask(agentId: "agent-1", status: "Completed", completedAt: now),
            MakeTask(agentId: "agent-2", status: "Completed", completedAt: now),
            MakeTask(agentId: "agent-2", status: "Completed", completedAt: now));

        var result = await _sut.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal("agent-2", result.AgentEffectiveness[0].AgentId);
        Assert.Equal("agent-1", result.AgentEffectiveness[1].AgentId);
    }
}
