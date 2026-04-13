using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="TaskAnalyticsService"/> — task cycle effectiveness metrics.
/// </summary>
public sealed class TaskAnalyticsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly TaskAnalyticsService _service;

    private static readonly AgentCatalogOptions TestCatalog = new(
        DefaultRoomId: "main",
        DefaultRoomName: "Main Room",
        Agents: new List<AgentDefinition>
        {
            new("agent-1", "Hephaestus", "Developer", "Backend engineer", "", "gpt-4", new(), new(), true),
            new("agent-2", "Athena", "Developer", "Frontend engineer", "", "gpt-4", new(), new(), true),
        });

    public TaskAnalyticsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _service = new TaskAnalyticsService(_db, TestCatalog);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helper ──

    private static int _taskCounter;

    private TaskEntity CreateTask(
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
            Status = status,
            Type = type,
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

    // ── Empty state ──

    [Fact]
    public async Task EmptyDatabase_ReturnsZeroedMetrics()
    {
        var result = await _service.GetTaskCycleAnalyticsAsync(null);

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
    public async Task StatusCounts_ReflectsAllStatuses()
    {
        _db.Tasks.AddRange(
            CreateTask(status: "Active"),
            CreateTask(status: "Active"),
            CreateTask(status: "Completed", completedAt: DateTime.UtcNow),
            CreateTask(status: "Blocked"),
            CreateTask(status: "Cancelled"),
            CreateTask(status: "InReview"),
            CreateTask(status: "Queued"));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(2, result.Overview.StatusCounts.Active);
        Assert.Equal(1, result.Overview.StatusCounts.Completed);
        Assert.Equal(1, result.Overview.StatusCounts.Blocked);
        Assert.Equal(1, result.Overview.StatusCounts.Cancelled);
        Assert.Equal(1, result.Overview.StatusCounts.InReview);
        Assert.Equal(1, result.Overview.StatusCounts.Queued);
    }

    // ── Completion rate ──

    [Fact]
    public async Task CompletionRate_CalculatedCorrectly()
    {
        var now = DateTime.UtcNow;
        _db.Tasks.AddRange(
            CreateTask(status: "Completed", completedAt: now),
            CreateTask(status: "Completed", completedAt: now),
            CreateTask(status: "Active"),
            CreateTask(status: "Cancelled"));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(4, result.Overview.TotalTasks);
        Assert.Equal(0.5, result.Overview.CompletionRate);
    }

    // ── Cycle time ──

    [Fact]
    public async Task AvgCycleTime_ComputedFromCreatedToCompleted()
    {
        var now = DateTime.UtcNow;
        _db.Tasks.AddRange(
            CreateTask(
                status: "Completed",
                createdAt: now.AddHours(-10),
                completedAt: now),
            CreateTask(
                status: "Completed",
                createdAt: now.AddHours(-20),
                completedAt: now));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        Assert.NotNull(result.Overview.AvgCycleTimeHours);
        Assert.Equal(15.0, result.Overview.AvgCycleTimeHours!.Value, 1);
    }

    // ── Queue time ──

    [Fact]
    public async Task AvgQueueTime_ComputedFromCreatedToStarted()
    {
        var now = DateTime.UtcNow;
        _db.Tasks.AddRange(
            CreateTask(
                status: "Completed",
                createdAt: now.AddHours(-10),
                startedAt: now.AddHours(-8),
                completedAt: now),
            CreateTask(
                status: "Completed",
                createdAt: now.AddHours(-20),
                startedAt: now.AddHours(-16),
                completedAt: now));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        // (2 + 4) / 2 = 3
        Assert.NotNull(result.Overview.AvgQueueTimeHours);
        Assert.Equal(3.0, result.Overview.AvgQueueTimeHours!.Value, 1);
    }

    // ── Execution span ──

    [Fact]
    public async Task AvgExecutionSpan_ComputedFromStartedToCompleted()
    {
        var now = DateTime.UtcNow;
        _db.Tasks.AddRange(
            CreateTask(
                status: "Completed",
                createdAt: now.AddHours(-10),
                startedAt: now.AddHours(-8),
                completedAt: now),
            CreateTask(
                status: "Completed",
                createdAt: now.AddHours(-20),
                startedAt: now.AddHours(-16),
                completedAt: now));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        // (8 + 16) / 2 = 12
        Assert.NotNull(result.Overview.AvgExecutionSpanHours);
        Assert.Equal(12.0, result.Overview.AvgExecutionSpanHours!.Value, 1);
    }

    // ── Review rounds ──

    [Fact]
    public async Task AvgReviewRounds_OnlyCountsTasksWithReviews()
    {
        var now = DateTime.UtcNow;
        _db.Tasks.AddRange(
            CreateTask(status: "Completed", completedAt: now, reviewRounds: 2),
            CreateTask(status: "Completed", completedAt: now, reviewRounds: 4),
            CreateTask(status: "Completed", completedAt: now, reviewRounds: 0));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        // (2 + 4) / 2 = 3 (excludes the 0-round task)
        Assert.NotNull(result.Overview.AvgReviewRounds);
        Assert.Equal(3.0, result.Overview.AvgReviewRounds!.Value, 1);
    }

    // ── Rework rate ──

    [Fact]
    public async Task ReworkRate_TasksWithMultipleReviewRounds()
    {
        var now = DateTime.UtcNow;
        _db.Tasks.AddRange(
            CreateTask(status: "Completed", completedAt: now, reviewRounds: 1),
            CreateTask(status: "Completed", completedAt: now, reviewRounds: 3),
            CreateTask(status: "Completed", completedAt: now, reviewRounds: 2),
            CreateTask(status: "Completed", completedAt: now, reviewRounds: 0));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        // 2 of 4 completed tasks have reviewRounds > 1
        Assert.Equal(0.5, result.Overview.ReworkRate);
    }

    // ── Per-agent effectiveness ──

    [Fact]
    public async Task AgentEffectiveness_PerAgentBreakdown()
    {
        var now = DateTime.UtcNow;
        _db.Tasks.AddRange(
            CreateTask(agentId: "agent-1", status: "Completed", completedAt: now, reviewRounds: 1, commitCount: 3),
            CreateTask(agentId: "agent-1", status: "Completed", completedAt: now, reviewRounds: 2, commitCount: 5),
            CreateTask(agentId: "agent-1", status: "Active"),
            CreateTask(agentId: "agent-2", status: "Completed", completedAt: now, reviewRounds: 1, commitCount: 2));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(2, result.AgentEffectiveness.Count);

        var a1 = result.AgentEffectiveness.First(a => a.AgentId == "agent-1");
        Assert.Equal("Hephaestus", a1.AgentName);
        Assert.Equal(3, a1.Assigned);
        Assert.Equal(2, a1.Completed);
        Assert.Equal(0.5, a1.FirstPassApprovalRate); // 1 of 2 completed with reviewRounds <= 1
        Assert.Equal(0.5, a1.ReworkRate); // 1 of 2 completed with reviewRounds > 1

        var a2 = result.AgentEffectiveness.First(a => a.AgentId == "agent-2");
        Assert.Equal("Athena", a2.AgentName);
        Assert.Equal(1, a2.Assigned);
        Assert.Equal(1, a2.Completed);
        Assert.Equal(1.0, a2.FirstPassApprovalRate);
    }

    // ── Time windowing ──

    [Fact]
    public async Task TimeWindow_FiltersByCompletedAtForMetrics()
    {
        var now = DateTime.UtcNow;
        // Old task: completed 48 hours ago
        _db.Tasks.Add(CreateTask(
            status: "Completed",
            createdAt: now.AddHours(-72),
            completedAt: now.AddHours(-48)));
        // Recent task: completed 2 hours ago
        _db.Tasks.Add(CreateTask(
            status: "Completed",
            createdAt: now.AddHours(-10),
            completedAt: now.AddHours(-2)));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(hoursBack: 24);

        // Only the recent completion should be in cycle time metrics
        Assert.NotNull(result.Overview.AvgCycleTimeHours);
        Assert.Equal(8.0, result.Overview.AvgCycleTimeHours!.Value, 1);
    }

    [Fact]
    public async Task CompletionRate_NeverExceeds100Percent_WithTimeWindow()
    {
        var now = DateTime.UtcNow;
        // Task created 48 hours ago but completed 2 hours ago — outside
        // creation window but inside completion window
        _db.Tasks.Add(CreateTask(
            status: "Completed",
            createdAt: now.AddHours(-48),
            completedAt: now.AddHours(-2)));
        // Task created and completed inside window
        _db.Tasks.Add(CreateTask(
            status: "Completed",
            createdAt: now.AddHours(-10),
            completedAt: now.AddHours(-1)));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(hoursBack: 24);

        // Both completed tasks should be counted; the one created outside the
        // window is still in the union denominator via its completed-in-window ID.
        Assert.True(result.Overview.CompletionRate <= 1.0,
            $"Completion rate {result.Overview.CompletionRate} should not exceed 1.0");
        Assert.Equal(2, result.Overview.TotalTasks);
    }

    // ── Type breakdown ──

    [Fact]
    public async Task TypeBreakdown_CountsByTaskType()
    {
        _db.Tasks.AddRange(
            CreateTask(type: "Feature"),
            CreateTask(type: "Feature"),
            CreateTask(type: "Bug"),
            CreateTask(type: "Chore"),
            CreateTask(type: "Spike"),
            CreateTask(type: "Spike"));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(2, result.TypeBreakdown.Feature);
        Assert.Equal(1, result.TypeBreakdown.Bug);
        Assert.Equal(1, result.TypeBreakdown.Chore);
        Assert.Equal(2, result.TypeBreakdown.Spike);
    }

    // ── Throughput buckets ──

    [Fact]
    public async Task ThroughputBuckets_Always12Buckets()
    {
        _db.Tasks.Add(CreateTask(
            status: "Completed",
            createdAt: DateTime.UtcNow.AddHours(-5),
            completedAt: DateTime.UtcNow));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(hoursBack: 24);

        Assert.Equal(12, result.ThroughputBuckets.Count);
        Assert.Equal(1, result.ThroughputBuckets.Sum(b => b.Completed));
    }

    // ── Unassigned tasks don't appear in agent effectiveness ──

    [Fact]
    public async Task UnassignedTasks_NotInAgentEffectiveness()
    {
        _db.Tasks.AddRange(
            CreateTask(agentId: null, status: "Queued"),
            CreateTask(agentId: null, status: "Active"));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        Assert.Empty(result.AgentEffectiveness);
        Assert.Equal(2, result.Overview.TotalTasks);
    }

    // ── Non-catalog agent falls back to ID ──

    [Fact]
    public async Task NonCatalogAgent_UsesIdAsName()
    {
        var now = DateTime.UtcNow;
        _db.Tasks.Add(CreateTask(agentId: "unknown-agent", status: "Completed", completedAt: now));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        var agent = Assert.Single(result.AgentEffectiveness);
        Assert.Equal("unknown-agent", agent.AgentName);
    }

    // ── Commits total ──

    [Fact]
    public async Task TotalCommits_SumsAcrossCompleted()
    {
        var now = DateTime.UtcNow;
        _db.Tasks.AddRange(
            CreateTask(status: "Completed", completedAt: now, commitCount: 5),
            CreateTask(status: "Completed", completedAt: now, commitCount: 3),
            CreateTask(status: "Active", commitCount: 10)); // not completed, not counted
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal(8, result.Overview.TotalCommits);
    }

    // ── Agent effectiveness ordered by completed count ──

    [Fact]
    public async Task AgentEffectiveness_OrderedByCompletedDescending()
    {
        var now = DateTime.UtcNow;
        _db.Tasks.AddRange(
            CreateTask(agentId: "agent-1", status: "Completed", completedAt: now),
            CreateTask(agentId: "agent-2", status: "Completed", completedAt: now),
            CreateTask(agentId: "agent-2", status: "Completed", completedAt: now));
        await _db.SaveChangesAsync();

        var result = await _service.GetTaskCycleAnalyticsAsync(null);

        Assert.Equal("agent-2", result.AgentEffectiveness[0].AgentId);
        Assert.Equal("agent-1", result.AgentEffectiveness[1].AgentId);
    }

    // ── Controller validation ──

    [Fact]
    public async Task Controller_RejectsInvalidHoursBack()
    {
        var controller = new AgentAcademy.Server.Controllers.AnalyticsController(
            null!, _service);

        var result = await controller.GetTaskAnalytics(hoursBack: 0);
        Assert.IsType<BadRequestObjectResult>(result.Result);

        result = await controller.GetTaskAnalytics(hoursBack: 9000);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Controller_ReturnsOkForValidRequest()
    {
        var controller = new AgentAcademy.Server.Controllers.AnalyticsController(
            null!, _service);

        var result = await controller.GetTaskAnalytics(hoursBack: 24);
        Assert.IsType<OkObjectResult>(result.Result);
    }
}
