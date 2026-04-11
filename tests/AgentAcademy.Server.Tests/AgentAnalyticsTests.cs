using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class AgentAnalyticsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentAnalyticsService _service;

    private static readonly AgentCatalogOptions TestCatalog = new(
        DefaultRoomId: "main",
        DefaultRoomName: "Main Room",
        Agents: new List<AgentDefinition>
        {
            new("planner-1", "Planner", "Planner", "Plans tasks", "", "gpt-4", new(), new(), true),
            new("coder-1", "Coder", "Coder", "Writes code", "", "gpt-4", new(), new(), true),
        }
    );

    public AgentAnalyticsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        _service = new AgentAnalyticsService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            TestCatalog,
            NullLogger<AgentAnalyticsService>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private AgentAcademyDbContext GetDb()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    }

    // ── Empty state ─────────────────────────────────────────────

    [Fact]
    public async Task GetAnalytics_NoData_ReturnsEmptySummary()
    {
        var result = await _service.GetAnalyticsSummaryAsync(hoursBack: 24);

        Assert.Empty(result.Agents);
        Assert.Equal(0, result.TotalRequests);
        Assert.Equal(0, result.TotalCost);
        Assert.Equal(0, result.TotalErrors);
    }

    // ── Usage aggregation ───────────────────────────────────────

    [Fact]
    public async Task GetAnalytics_WithUsageData_AggregatesPerAgent()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.AddRange(
                MakeUsage("planner-1", inputTokens: 100, outputTokens: 50, cost: 0.01, durationMs: 500),
                MakeUsage("planner-1", inputTokens: 200, outputTokens: 100, cost: 0.02, durationMs: 600),
                MakeUsage("coder-1", inputTokens: 1000, outputTokens: 500, cost: 0.10, durationMs: 1000));
            await db.SaveChangesAsync();
        }

        var result = await _service.GetAnalyticsSummaryAsync(hoursBack: null);

        Assert.Equal(2, result.Agents.Count);
        Assert.Equal(3, result.TotalRequests);

        // Coder has more requests-worth of tokens, but planner has 2 requests
        // Agents are sorted by TotalRequests descending
        var planner = result.Agents.First(a => a.AgentId == "planner-1");
        Assert.Equal(2, planner.TotalRequests);
        Assert.Equal(300, planner.TotalInputTokens);
        Assert.Equal(150, planner.TotalOutputTokens);
        Assert.Equal(0.03, planner.TotalCost, precision: 4);
        Assert.NotNull(planner.AverageResponseTimeMs);
        Assert.Equal(550, planner.AverageResponseTimeMs!.Value, precision: 1);
        Assert.Equal("Planner", planner.AgentName);

        var coder = result.Agents.First(a => a.AgentId == "coder-1");
        Assert.Equal(1, coder.TotalRequests);
        Assert.Equal(1000, coder.TotalInputTokens);
        Assert.Equal("Coder", coder.AgentName);
    }

    // ── Error aggregation ───────────────────────────────────────

    [Fact]
    public async Task GetAnalytics_WithErrors_AggregatesRecoverableAndUnrecoverable()
    {
        using (var db = GetDb())
        {
            db.AgentErrors.AddRange(
                MakeError("planner-1", recoverable: true),
                MakeError("planner-1", recoverable: true),
                MakeError("planner-1", recoverable: false),
                MakeError("coder-1", recoverable: false));
            await db.SaveChangesAsync();
        }

        var result = await _service.GetAnalyticsSummaryAsync(hoursBack: null);

        Assert.Equal(4, result.TotalErrors);

        var planner = result.Agents.First(a => a.AgentId == "planner-1");
        Assert.Equal(3, planner.TotalErrors);
        Assert.Equal(2, planner.RecoverableErrors);
        Assert.Equal(1, planner.UnrecoverableErrors);

        var coder = result.Agents.First(a => a.AgentId == "coder-1");
        Assert.Equal(1, coder.TotalErrors);
        Assert.Equal(0, coder.RecoverableErrors);
        Assert.Equal(1, coder.UnrecoverableErrors);
    }

    // ── Task aggregation ────────────────────────────────────────

    [Fact]
    public async Task GetAnalytics_WithTasks_CountsAssignedAndCompleted()
    {
        using (var db = GetDb())
        {
            db.Tasks.AddRange(
                MakeTask("planner-1", status: "Active"),
                MakeTask("planner-1", status: "Completed"),
                MakeTask("planner-1", status: "Completed"),
                MakeTask("coder-1", status: "Active"));
            await db.SaveChangesAsync();
        }

        var result = await _service.GetAnalyticsSummaryAsync(hoursBack: null);

        var planner = result.Agents.First(a => a.AgentId == "planner-1");
        Assert.Equal(3, planner.TasksAssigned);
        Assert.Equal(2, planner.TasksCompleted);

        var coder = result.Agents.First(a => a.AgentId == "coder-1");
        Assert.Equal(1, coder.TasksAssigned);
        Assert.Equal(0, coder.TasksCompleted);
    }

    // ── Time window filtering ───────────────────────────────────

    [Fact]
    public async Task GetAnalytics_WithHoursBack_ExcludesOldData()
    {
        var recent = DateTime.UtcNow.AddHours(-1);
        var old = DateTime.UtcNow.AddHours(-48);

        using (var db = GetDb())
        {
            db.LlmUsage.AddRange(
                MakeUsage("planner-1", inputTokens: 100, recordedAt: recent),
                MakeUsage("planner-1", inputTokens: 500, recordedAt: old));
            db.AgentErrors.AddRange(
                MakeError("planner-1", occurredAt: recent),
                MakeError("planner-1", occurredAt: old));
            await db.SaveChangesAsync();
        }

        var result = await _service.GetAnalyticsSummaryAsync(hoursBack: 24);

        Assert.Single(result.Agents);
        var planner = result.Agents[0];
        Assert.Equal(1, planner.TotalRequests);
        Assert.Equal(100, planner.TotalInputTokens);
        Assert.Equal(1, planner.TotalErrors);
    }

    // ── Token trend ─────────────────────────────────────────────

    [Fact]
    public async Task GetAnalytics_TokenTrend_Has12Buckets()
    {
        using (var db = GetDb())
        {
            // Spread records across the 24h window
            for (int i = 0; i < 24; i++)
            {
                db.LlmUsage.Add(MakeUsage("planner-1",
                    inputTokens: 100,
                    outputTokens: 0,
                    recordedAt: DateTime.UtcNow.AddHours(-23 + i)));
            }
            await db.SaveChangesAsync();
        }

        var result = await _service.GetAnalyticsSummaryAsync(hoursBack: 24);

        var planner = result.Agents.First(a => a.AgentId == "planner-1");
        Assert.Equal(12, planner.TokenTrend.Count);
        Assert.True(planner.TokenTrend.Sum() > 0);
    }

    // ── Agent name resolution ───────────────────────────────────

    [Fact]
    public async Task GetAnalytics_UnknownAgent_UsesAgentIdAsName()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("unknown-agent", inputTokens: 50));
            await db.SaveChangesAsync();
        }

        var result = await _service.GetAnalyticsSummaryAsync(hoursBack: null);

        var unknown = result.Agents.First(a => a.AgentId == "unknown-agent");
        Assert.Equal("unknown-agent", unknown.AgentName);
    }

    // ── Cross-source agent merge ────────────────────────────────

    [Fact]
    public async Task GetAnalytics_AgentWithOnlyErrors_StillAppears()
    {
        using (var db = GetDb())
        {
            // planner-1 only has errors, no usage or tasks
            db.AgentErrors.Add(MakeError("planner-1", recoverable: true));
            await db.SaveChangesAsync();
        }

        var result = await _service.GetAnalyticsSummaryAsync(hoursBack: null);

        Assert.Single(result.Agents);
        var planner = result.Agents[0];
        Assert.Equal("planner-1", planner.AgentId);
        Assert.Equal(0, planner.TotalRequests);
        Assert.Equal(1, planner.TotalErrors);
    }

    [Fact]
    public async Task GetAnalytics_WindowDates_AreReasonable()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("planner-1", inputTokens: 100));
            await db.SaveChangesAsync();
        }

        var before = DateTimeOffset.UtcNow;
        var result = await _service.GetAnalyticsSummaryAsync(hoursBack: 24);
        var after = DateTimeOffset.UtcNow;

        Assert.True(result.WindowEnd >= before && result.WindowEnd <= after);
        Assert.True(result.WindowStart < result.WindowEnd);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static int _usageId;
    private static int _errorId;
    private static int _taskId;

    private static LlmUsageEntity MakeUsage(
        string agentId,
        long inputTokens = 0,
        long outputTokens = 0,
        double? cost = null,
        int? durationMs = null,
        DateTime? recordedAt = null)
    {
        return new LlmUsageEntity
        {
            Id = Interlocked.Increment(ref _usageId).ToString(),
            AgentId = agentId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = cost,
            DurationMs = durationMs,
            RecordedAt = recordedAt ?? DateTime.UtcNow,
        };
    }

    private static AgentErrorEntity MakeError(
        string agentId,
        bool recoverable = true,
        DateTime? occurredAt = null)
    {
        return new AgentErrorEntity
        {
            Id = Interlocked.Increment(ref _errorId).ToString(),
            AgentId = agentId,
            ErrorType = "TestError",
            Message = "test error",
            Recoverable = recoverable,
            Retried = false,
            OccurredAt = occurredAt ?? DateTime.UtcNow,
        };
    }

    private static TaskEntity MakeTask(
        string agentId,
        string status = "Active")
    {
        return new TaskEntity
        {
            Id = $"task-{Interlocked.Increment(ref _taskId)}",
            Title = "Test task",
            AssignedAgentId = agentId,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
