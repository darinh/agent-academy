using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Controller-level tests for <see cref="AnalyticsController"/> — parameter
/// validation, HTTP status codes, and end-to-end response shapes.
/// Uses real services with in-memory SQLite.
/// </summary>
public sealed class AnalyticsControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AnalyticsController _controller;
    private static int _usageId;
    private static int _errorId;
    private static int _taskId;

    private static readonly AgentCatalogOptions TestCatalog = new(
        DefaultRoomId: "main",
        DefaultRoomName: "Main Room",
        Agents: new List<AgentDefinition>
        {
            new("planner-1", "Planner", "Planner", "Plans tasks", "", "gpt-4", new(), new(), true),
            new("coder-1", "Coder", "Coder", "Writes code", "", "gpt-4", new(), new(), true),
        }
    );

    public AnalyticsControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var analytics = new AgentAnalyticsService(
            scopeFactory, TestCatalog, NullLogger<AgentAnalyticsService>.Instance);

        _controller = new AnalyticsController(analytics);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Creates a scoped <see cref="AgentAcademyDbContext"/>. The caller must
    /// dispose the returned wrapper to release both the scope and the context.
    /// </summary>
    private ScopedDb GetDb() => new(_serviceProvider.CreateScope());

    private sealed class ScopedDb : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly AgentAcademyDbContext _db;
        public ScopedDb(IServiceScope scope)
        {
            _scope = scope;
            _db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        }
        public AgentAcademyDbContext Db => _db;
        public void Dispose() => _scope.Dispose();

        // Convenience pass-throughs to reduce churn in test bodies
        public DbSet<LlmUsageEntity> LlmUsage => _db.LlmUsage;
        public DbSet<AgentErrorEntity> AgentErrors => _db.AgentErrors;
        public DbSet<TaskEntity> Tasks => _db.Tasks;
        public Task<int> SaveChangesAsync() => _db.SaveChangesAsync();
    }

    // ── GetAgentAnalytics ───────────────────────────────────────

    [Fact]
    public async Task GetAgentAnalytics_NoData_Returns200WithEmptyAgents()
    {
        var result = await _controller.GetAgentAnalytics();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<AgentAnalyticsSummary>(ok.Value);
        Assert.Empty(summary.Agents);
        Assert.Equal(0, summary.TotalRequests);
        Assert.Equal(0, summary.TotalErrors);
        Assert.Equal(0.0, summary.TotalCost);
    }

    [Fact]
    public async Task GetAgentAnalytics_WithData_ReturnsAggregatedMetrics()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.AddRange(
                MakeUsage("planner-1", inputTokens: 100, outputTokens: 50, cost: 0.01),
                MakeUsage("planner-1", inputTokens: 200, outputTokens: 100, cost: 0.02),
                MakeUsage("coder-1", inputTokens: 500, outputTokens: 250, cost: 0.05));
            db.AgentErrors.Add(MakeError("coder-1", recoverable: true));
            db.Tasks.Add(MakeTask("planner-1", status: "Completed", completedAt: DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var result = await _controller.GetAgentAnalytics();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<AgentAnalyticsSummary>(ok.Value);
        Assert.Equal(3, summary.TotalRequests);
        Assert.Equal(1, summary.TotalErrors);
        Assert.True(summary.TotalCost > 0);
        Assert.True(summary.Agents.Count >= 2);

        var planner = summary.Agents.First(a => a.AgentId == "planner-1");
        Assert.Equal(2, planner.TotalRequests);
        Assert.Equal(300, planner.TotalInputTokens);
        Assert.Equal(150, planner.TotalOutputTokens);
        Assert.Equal(1, planner.TasksCompleted);
    }

    [Fact]
    public async Task GetAgentAnalytics_NullHoursBack_ReturnsAllTimeData()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("planner-1", inputTokens: 100,
                recordedAt: DateTime.UtcNow.AddDays(-365)));
            db.LlmUsage.Add(MakeUsage("planner-1", inputTokens: 200,
                recordedAt: DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var result = await _controller.GetAgentAnalytics(hoursBack: null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<AgentAnalyticsSummary>(ok.Value);
        Assert.Equal(2, summary.TotalRequests);
    }

    [Fact]
    public async Task GetAgentAnalytics_HoursBack24_FiltersOldRecords()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("planner-1", inputTokens: 100,
                recordedAt: DateTime.UtcNow.AddHours(-48)));
            db.LlmUsage.Add(MakeUsage("planner-1", inputTokens: 200,
                recordedAt: DateTime.UtcNow.AddHours(-1)));
            await db.SaveChangesAsync();
        }

        var result = await _controller.GetAgentAnalytics(hoursBack: 24);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<AgentAnalyticsSummary>(ok.Value);
        Assert.Equal(1, summary.TotalRequests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task GetAgentAnalytics_HoursBackTooLow_Returns400(int hoursBack)
    {
        var result = await _controller.GetAgentAnalytics(hoursBack: hoursBack);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(bad.Value);
    }

    [Fact]
    public async Task GetAgentAnalytics_HoursBackTooHigh_Returns400()
    {
        var result = await _controller.GetAgentAnalytics(hoursBack: 8761);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetAgentAnalytics_HoursBackMaxValid_Returns200()
    {
        var result = await _controller.GetAgentAnalytics(hoursBack: 8760);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // ── GetAgentDetail ──────────────────────────────────────────

    [Fact]
    public async Task GetAgentDetail_KnownAgent_Returns200WithMetrics()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.AddRange(
                MakeUsage("coder-1", inputTokens: 400, outputTokens: 200, cost: 0.04, model: "gpt-4"),
                MakeUsage("coder-1", inputTokens: 100, outputTokens: 50, cost: 0.01, model: "gpt-3.5"));
            db.AgentErrors.Add(MakeError("coder-1"));
            db.Tasks.Add(MakeTask("coder-1", status: "Active"));
            await db.SaveChangesAsync();
        }

        var result = await _controller.GetAgentDetail("coder-1");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<AgentAnalyticsDetail>(ok.Value);
        Assert.Equal("coder-1", detail.Agent.AgentId);
        Assert.Equal("Coder", detail.Agent.AgentName);
        Assert.Equal(2, detail.Agent.TotalRequests);
        Assert.Equal(2, detail.RecentRequests.Count);
        Assert.Single(detail.RecentErrors);
        Assert.Single(detail.Tasks);
        Assert.Equal(2, detail.ModelBreakdown.Count);
    }

    [Fact]
    public async Task GetAgentDetail_UnknownAgent_Returns200WithEmptyData()
    {
        var result = await _controller.GetAgentDetail("nonexistent");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<AgentAnalyticsDetail>(ok.Value);
        Assert.Equal("nonexistent", detail.Agent.AgentId);
        Assert.Equal(0, detail.Agent.TotalRequests);
        Assert.Empty(detail.RecentRequests);
        Assert.Empty(detail.RecentErrors);
        Assert.Empty(detail.Tasks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8761)]
    public async Task GetAgentDetail_InvalidHoursBack_Returns400(int hoursBack)
    {
        var result = await _controller.GetAgentDetail("coder-1", hoursBack: hoursBack);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(-5)]
    public async Task GetAgentDetail_InvalidRequestLimit_Returns400(int limit)
    {
        var result = await _controller.GetAgentDetail("coder-1", requestLimit: limit);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task GetAgentDetail_InvalidErrorLimit_Returns400(int limit)
    {
        var result = await _controller.GetAgentDetail("coder-1", errorLimit: limit);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task GetAgentDetail_InvalidTaskLimit_Returns400(int limit)
    {
        var result = await _controller.GetAgentDetail("coder-1", taskLimit: limit);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetAgentDetail_CustomLimits_RespectedInResults()
    {
        using (var db = GetDb())
        {
            for (int i = 0; i < 10; i++)
            {
                db.LlmUsage.Add(MakeUsage("planner-1", inputTokens: 100));
                db.AgentErrors.Add(MakeError("planner-1"));
                db.Tasks.Add(MakeTask("planner-1"));
            }
            await db.SaveChangesAsync();
        }

        var result = await _controller.GetAgentDetail("planner-1",
            requestLimit: 3, errorLimit: 2, taskLimit: 5);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<AgentAnalyticsDetail>(ok.Value);
        Assert.True(detail.RecentRequests.Count <= 3);
        Assert.True(detail.RecentErrors.Count <= 2);
        Assert.True(detail.Tasks.Count <= 5);
    }

    [Fact]
    public async Task GetAgentDetail_BoundaryLimits_AcceptMinMax()
    {
        // requestLimit=1 and requestLimit=200 are both valid
        var resultMin = await _controller.GetAgentDetail("coder-1", requestLimit: 1, errorLimit: 1, taskLimit: 1);
        Assert.IsType<OkObjectResult>(resultMin.Result);

        var resultMax = await _controller.GetAgentDetail("coder-1", requestLimit: 200, errorLimit: 200, taskLimit: 200);
        Assert.IsType<OkObjectResult>(resultMax.Result);
    }

    [Fact]
    public async Task GetAgentDetail_HoursBack_FiltersRecords()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("coder-1", inputTokens: 100,
                recordedAt: DateTime.UtcNow.AddHours(-48)));
            db.LlmUsage.Add(MakeUsage("coder-1", inputTokens: 200,
                recordedAt: DateTime.UtcNow.AddHours(-1)));
            await db.SaveChangesAsync();
        }

        var result = await _controller.GetAgentDetail("coder-1", hoursBack: 24);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<AgentAnalyticsDetail>(ok.Value);
        Assert.Equal(1, detail.Agent.TotalRequests);
        Assert.Single(detail.RecentRequests);
    }

    [Fact]
    public async Task GetAgentDetail_ModelBreakdown_IncludesAllModels()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.AddRange(
                MakeUsage("coder-1", inputTokens: 100, cost: 0.01, model: "gpt-4"),
                MakeUsage("coder-1", inputTokens: 100, cost: 0.01, model: "gpt-4"),
                MakeUsage("coder-1", inputTokens: 50, cost: 0.005, model: "claude-3"));
            await db.SaveChangesAsync();
        }

        var result = await _controller.GetAgentDetail("coder-1");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<AgentAnalyticsDetail>(ok.Value);
        Assert.Equal(2, detail.ModelBreakdown.Count);
        var gpt4 = detail.ModelBreakdown.First(m => m.Model == "gpt-4");
        Assert.Equal(2, gpt4.Requests);
    }

    [Fact]
    public async Task GetAgentDetail_ActivityBuckets_Populated()
    {
        using (var db = GetDb())
        {
            db.LlmUsage.Add(MakeUsage("coder-1", inputTokens: 100,
                recordedAt: DateTime.UtcNow.AddHours(-2)));
            db.LlmUsage.Add(MakeUsage("coder-1", inputTokens: 200,
                recordedAt: DateTime.UtcNow.AddMinutes(-30)));
            await db.SaveChangesAsync();
        }

        var result = await _controller.GetAgentDetail("coder-1", hoursBack: 24);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<AgentAnalyticsDetail>(ok.Value);
        Assert.NotEmpty(detail.ActivityBuckets);
        Assert.True(detail.ActivityBuckets.Sum(b => b.Requests) >= 2);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static LlmUsageEntity MakeUsage(
        string agentId,
        long inputTokens = 0,
        long outputTokens = 0,
        double? cost = null,
        int? durationMs = null,
        DateTime? recordedAt = null,
        string? model = null)
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
            Model = model,
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
        string status = "Active",
        DateTime? createdAt = null,
        DateTime? completedAt = null)
    {
        return new TaskEntity
        {
            Id = $"task-{Interlocked.Increment(ref _taskId)}",
            Title = "Test task",
            AssignedAgentId = agentId,
            Status = status,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CompletedAt = completedAt,
        };
    }
}
