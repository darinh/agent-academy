using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class LlmUsageTrackerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly LlmUsageTracker _sut;

    public LlmUsageTrackerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        _sut = new LlmUsageTracker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LlmUsageTracker>.Instance);
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

    private static LlmUsageEntity CreateUsageEntity(
        string agentId = "agent-1",
        string? roomId = "room-1",
        string? model = "gpt-5",
        long inputTokens = 100,
        long outputTokens = 50,
        long cacheReadTokens = 0,
        long cacheWriteTokens = 0,
        double? cost = 0.01,
        int? durationMs = 500,
        string? apiCallId = null,
        string? initiator = null,
        string? reasoningEffort = null,
        DateTime? recordedAt = null)
    {
        return new LlmUsageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            AgentId = agentId,
            RoomId = roomId,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            Cost = cost,
            DurationMs = durationMs,
            ApiCallId = apiCallId,
            Initiator = initiator,
            ReasoningEffort = reasoningEffort,
            RecordedAt = recordedAt ?? DateTime.UtcNow,
        };
    }

    private async Task SeedAsync(params LlmUsageEntity[] entities)
    {
        using var db = GetDb();
        db.LlmUsage.AddRange(entities);
        await db.SaveChangesAsync();
    }

    // ── RecordAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task Record_PersistsToDatabase()
    {
        await _sut.RecordAsync(
            "agent-1", "room-1", "gpt-5",
            100, 50, 20, 10, 0.005, 1234,
            "call-abc", "human", "medium");

        using var db = GetDb();
        var record = await db.LlmUsage.SingleAsync();

        Assert.Equal("agent-1", record.AgentId);
        Assert.Equal("room-1", record.RoomId);
        Assert.Equal("gpt-5", record.Model);
        Assert.Equal(100, record.InputTokens);
        Assert.Equal(50, record.OutputTokens);
        Assert.Equal(20, record.CacheReadTokens);
        Assert.Equal(10, record.CacheWriteTokens);
        Assert.Equal(0.005, record.Cost);
        Assert.Equal(1234, record.DurationMs);
        Assert.Equal("call-abc", record.ApiCallId);
        Assert.Equal("human", record.Initiator);
        Assert.Equal("medium", record.ReasoningEffort);
    }

    [Fact]
    public async Task Record_GeneratesUniqueId()
    {
        await _sut.RecordAsync("a1", null, null, 0, 0, 0, 0, null, null, null, null, null);
        await _sut.RecordAsync("a2", null, null, 0, 0, 0, 0, null, null, null, null, null);

        using var db = GetDb();
        var ids = await db.LlmUsage.Select(u => u.Id).ToListAsync();

        Assert.Equal(2, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotEmpty(id));
    }

    [Fact]
    public async Task Record_SetsRecordedAt()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        await _sut.RecordAsync("a1", null, null, 0, 0, 0, 0, null, null, null, null, null);
        var after = DateTime.UtcNow.AddSeconds(1);

        using var db = GetDb();
        var record = await db.LlmUsage.SingleAsync();

        Assert.InRange(record.RecordedAt, before, after);
    }

    [Fact]
    public async Task Record_NullOptionalFields_PersistsCorrectly()
    {
        await _sut.RecordAsync(
            "agent-1", null, null,
            null, null, null, null,
            null, null, null, null, null);

        using var db = GetDb();
        var record = await db.LlmUsage.SingleAsync();

        Assert.Null(record.RoomId);
        Assert.Null(record.Model);
        Assert.Null(record.Cost);
        Assert.Null(record.DurationMs);
        Assert.Null(record.ApiCallId);
        Assert.Null(record.Initiator);
        Assert.Null(record.ReasoningEffort);
    }

    [Fact]
    public async Task Record_NaNInputTokens_StoredAsZero()
    {
        await _sut.RecordAsync(
            "agent-1", null, null,
            double.NaN, 0, 0, 0,
            null, null, null, null, null);

        using var db = GetDb();
        var record = await db.LlmUsage.SingleAsync();
        Assert.Equal(0, record.InputTokens);
    }

    [Fact]
    public async Task Record_InfinityInputTokens_StoredAsZero()
    {
        await _sut.RecordAsync(
            "agent-1", null, null,
            double.PositiveInfinity, double.NegativeInfinity, 0, 0,
            null, null, null, null, null);

        using var db = GetDb();
        var record = await db.LlmUsage.SingleAsync();
        Assert.Equal(0, record.InputTokens);
        Assert.Equal(0, record.OutputTokens);
    }

    [Fact]
    public async Task Record_NullInputTokens_StoredAsZero()
    {
        await _sut.RecordAsync(
            "agent-1", null, null,
            null, null, null, null,
            null, null, null, null, null);

        using var db = GetDb();
        var record = await db.LlmUsage.SingleAsync();
        Assert.Equal(0, record.InputTokens);
        Assert.Equal(0, record.OutputTokens);
        Assert.Equal(0, record.CacheReadTokens);
        Assert.Equal(0, record.CacheWriteTokens);
    }

    [Fact]
    public async Task Record_NonFiniteCost_StoredAsNull()
    {
        await _sut.RecordAsync(
            "agent-1", null, null,
            0, 0, 0, 0,
            double.NaN, null, null, null, null);

        using var db = GetDb();
        var record = await db.LlmUsage.SingleAsync();
        Assert.Null(record.Cost);
    }

    [Fact]
    public async Task Record_NaNDurationMs_StoredAsNull()
    {
        await _sut.RecordAsync(
            "agent-1", null, null,
            0, 0, 0, 0,
            null, double.NaN, null, null, null);

        using var db = GetDb();
        var record = await db.LlmUsage.SingleAsync();
        Assert.Null(record.DurationMs);
    }

    [Fact]
    public async Task Record_ValidCost_StoredCorrectly()
    {
        await _sut.RecordAsync(
            "agent-1", null, null,
            0, 0, 0, 0,
            0.12345, 750.0, null, null, null);

        using var db = GetDb();
        var record = await db.LlmUsage.SingleAsync();
        Assert.Equal(0.12345, record.Cost);
        Assert.Equal(750, record.DurationMs);
    }

    [Fact]
    public async Task Record_DbError_DoesNotThrow()
    {
        var brokenScopeFactory = Substitute.For<IServiceScopeFactory>();
        brokenScopeFactory.CreateScope()
            .Returns(_ => throw new InvalidOperationException("DB down"));

        var tracker = new LlmUsageTracker(brokenScopeFactory, NullLogger<LlmUsageTracker>.Instance);

        var ex = await Record.ExceptionAsync(() =>
            tracker.RecordAsync("a1", "r1", "m1", 100, 50, 0, 0, 0.01, 500, null, null, null));

        Assert.Null(ex);
    }

    // ── GetAgentUsageSinceAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetAgentUsageSince_NoData_ReturnsZeros()
    {
        var result = await _sut.GetAgentUsageSinceAsync("agent-1", DateTime.UtcNow.AddHours(-1));

        Assert.Equal(0, result.RequestCount);
        Assert.Equal(0, result.TotalTokens);
        Assert.Equal(0m, result.TotalCost);
    }

    [Fact]
    public async Task GetAgentUsageSince_AggregatesCorrectly()
    {
        await SeedAsync(
            CreateUsageEntity(agentId: "agent-1", inputTokens: 100, outputTokens: 50, cost: 0.01),
            CreateUsageEntity(agentId: "agent-1", inputTokens: 200, outputTokens: 100, cost: 0.02));

        var result = await _sut.GetAgentUsageSinceAsync("agent-1", DateTime.UtcNow.AddHours(-1));

        Assert.Equal(2, result.RequestCount);
        Assert.Equal(450, result.TotalTokens); // (100+50) + (200+100)
        Assert.Equal(0.03m, result.TotalCost);
    }

    [Fact]
    public async Task GetAgentUsageSince_FiltersByAgentId()
    {
        await SeedAsync(
            CreateUsageEntity(agentId: "agent-1", inputTokens: 100, outputTokens: 50),
            CreateUsageEntity(agentId: "agent-2", inputTokens: 200, outputTokens: 100));

        var result = await _sut.GetAgentUsageSinceAsync("agent-1", DateTime.UtcNow.AddHours(-1));

        Assert.Equal(1, result.RequestCount);
        Assert.Equal(150, result.TotalTokens);
    }

    [Fact]
    public async Task GetAgentUsageSince_FiltersBySince()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateUsageEntity(agentId: "agent-1", inputTokens: 100, outputTokens: 50,
                recordedAt: now.AddHours(-48)),
            CreateUsageEntity(agentId: "agent-1", inputTokens: 200, outputTokens: 100,
                recordedAt: now.AddMinutes(-30)));

        var result = await _sut.GetAgentUsageSinceAsync("agent-1", now.AddHours(-1));

        Assert.Equal(1, result.RequestCount);
        Assert.Equal(300, result.TotalTokens);
    }

    [Fact]
    public async Task GetAgentUsageSince_IncludesRecordsAtExactSinceTime()
    {
        var exactTime = DateTime.UtcNow.AddMinutes(-30);
        await SeedAsync(
            CreateUsageEntity(agentId: "agent-1", inputTokens: 100, outputTokens: 50,
                recordedAt: exactTime));

        // Uses >= so exact time should be included
        var result = await _sut.GetAgentUsageSinceAsync("agent-1", exactTime);

        Assert.Equal(1, result.RequestCount);
    }

    // ── GetRoomUsageAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetRoomUsage_NoData_ReturnsZeros()
    {
        var result = await _sut.GetRoomUsageAsync("room-1");

        Assert.Equal(0, result.TotalInputTokens);
        Assert.Equal(0, result.TotalOutputTokens);
        Assert.Equal(0, result.TotalCost);
        Assert.Equal(0, result.RequestCount);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task GetRoomUsage_AggregatesCorrectly()
    {
        await SeedAsync(
            CreateUsageEntity(roomId: "room-1", inputTokens: 100, outputTokens: 50, cost: 0.01, model: "gpt-5"),
            CreateUsageEntity(roomId: "room-1", inputTokens: 200, outputTokens: 100, cost: 0.02, model: "claude-4"));

        var result = await _sut.GetRoomUsageAsync("room-1");

        Assert.Equal(300, result.TotalInputTokens);
        Assert.Equal(150, result.TotalOutputTokens);
        Assert.Equal(0.03, result.TotalCost);
        Assert.Equal(2, result.RequestCount);
    }

    [Fact]
    public async Task GetRoomUsage_FiltersByRoomId()
    {
        await SeedAsync(
            CreateUsageEntity(roomId: "room-1", inputTokens: 100, outputTokens: 50),
            CreateUsageEntity(roomId: "room-2", inputTokens: 200, outputTokens: 100));

        var result = await _sut.GetRoomUsageAsync("room-1");

        Assert.Equal(1, result.RequestCount);
        Assert.Equal(100, result.TotalInputTokens);
    }

    [Fact]
    public async Task GetRoomUsage_CollectsDistinctModels()
    {
        await SeedAsync(
            CreateUsageEntity(roomId: "room-1", model: "gpt-5"),
            CreateUsageEntity(roomId: "room-1", model: "claude-4"),
            CreateUsageEntity(roomId: "room-1", model: "gpt-5")); // duplicate

        var result = await _sut.GetRoomUsageAsync("room-1");

        Assert.Equal(2, result.Models.Count);
        Assert.Contains("gpt-5", result.Models);
        Assert.Contains("claude-4", result.Models);
    }

    [Fact]
    public async Task GetRoomUsage_NullModel_NotInModels()
    {
        await SeedAsync(
            CreateUsageEntity(roomId: "room-1", model: null),
            CreateUsageEntity(roomId: "room-1", model: "gpt-5"));

        var result = await _sut.GetRoomUsageAsync("room-1");

        Assert.Single(result.Models);
        Assert.Equal("gpt-5", result.Models[0]);
    }

    // ── GetRoomUsageByAgentAsync ────────────────────────────────────────

    [Fact]
    public async Task GetRoomUsageByAgent_NoData_ReturnsEmpty()
    {
        var result = await _sut.GetRoomUsageByAgentAsync("room-1");
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRoomUsageByAgent_GroupsByAgent()
    {
        await SeedAsync(
            CreateUsageEntity(agentId: "agent-1", roomId: "room-1", inputTokens: 100, outputTokens: 50, cost: 0.01),
            CreateUsageEntity(agentId: "agent-1", roomId: "room-1", inputTokens: 200, outputTokens: 100, cost: 0.02),
            CreateUsageEntity(agentId: "agent-2", roomId: "room-1", inputTokens: 150, outputTokens: 75, cost: 0.015));

        var result = await _sut.GetRoomUsageByAgentAsync("room-1");

        Assert.Equal(2, result.Count);

        var a1 = result.Single(r => r.AgentId == "agent-1");
        Assert.Equal(300, a1.TotalInputTokens);
        Assert.Equal(150, a1.TotalOutputTokens);
        Assert.Equal(0.03, a1.TotalCost);
        Assert.Equal(2, a1.RequestCount);

        var a2 = result.Single(r => r.AgentId == "agent-2");
        Assert.Equal(150, a2.TotalInputTokens);
        Assert.Equal(75, a2.TotalOutputTokens);
        Assert.Equal(1, a2.RequestCount);
    }

    [Fact]
    public async Task GetRoomUsageByAgent_FiltersByRoomId()
    {
        await SeedAsync(
            CreateUsageEntity(agentId: "agent-1", roomId: "room-1"),
            CreateUsageEntity(agentId: "agent-1", roomId: "room-2"));

        var result = await _sut.GetRoomUsageByAgentAsync("room-1");

        Assert.Single(result);
        Assert.Equal("agent-1", result[0].AgentId);
    }

    // ── GetGlobalUsageAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetGlobalUsage_NoData_ReturnsZeros()
    {
        var result = await _sut.GetGlobalUsageAsync();

        Assert.Equal(0, result.TotalInputTokens);
        Assert.Equal(0, result.TotalOutputTokens);
        Assert.Equal(0, result.TotalCost);
        Assert.Equal(0, result.RequestCount);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task GetGlobalUsage_AggregatesAll()
    {
        await SeedAsync(
            CreateUsageEntity(roomId: "room-1", inputTokens: 100, outputTokens: 50, cost: 0.01),
            CreateUsageEntity(roomId: "room-2", inputTokens: 200, outputTokens: 100, cost: 0.02));

        var result = await _sut.GetGlobalUsageAsync();

        Assert.Equal(300, result.TotalInputTokens);
        Assert.Equal(150, result.TotalOutputTokens);
        Assert.Equal(0.03, result.TotalCost);
        Assert.Equal(2, result.RequestCount);
    }

    [Fact]
    public async Task GetGlobalUsage_WithSince_Filters()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateUsageEntity(inputTokens: 100, outputTokens: 50, recordedAt: now.AddHours(-48)),
            CreateUsageEntity(inputTokens: 200, outputTokens: 100, recordedAt: now.AddMinutes(-30)));

        var result = await _sut.GetGlobalUsageAsync(since: now.AddHours(-1));

        Assert.Equal(1, result.RequestCount);
        Assert.Equal(200, result.TotalInputTokens);
    }

    [Fact]
    public async Task GetGlobalUsage_WithoutSince_ReturnsAll()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateUsageEntity(recordedAt: now.AddDays(-30)),
            CreateUsageEntity(recordedAt: now));

        var result = await _sut.GetGlobalUsageAsync(since: null);

        Assert.Equal(2, result.RequestCount);
    }

    [Fact]
    public async Task GetGlobalUsage_CollectsDistinctModels()
    {
        await SeedAsync(
            CreateUsageEntity(model: "gpt-5"),
            CreateUsageEntity(model: "claude-4"),
            CreateUsageEntity(model: "gpt-5"),
            CreateUsageEntity(model: null));

        var result = await _sut.GetGlobalUsageAsync();

        Assert.Equal(2, result.Models.Count);
        Assert.Contains("gpt-5", result.Models);
        Assert.Contains("claude-4", result.Models);
    }

    // ── GetRecentUsageAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetRecentUsage_NoData_ReturnsEmpty()
    {
        var result = await _sut.GetRecentUsageAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRecentUsage_OrderedByRecordedAtDesc()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateUsageEntity(inputTokens: 100, recordedAt: now.AddMinutes(-10)),
            CreateUsageEntity(inputTokens: 300, recordedAt: now),
            CreateUsageEntity(inputTokens: 200, recordedAt: now.AddMinutes(-5)));

        var result = await _sut.GetRecentUsageAsync();

        Assert.Equal(3, result.Count);
        Assert.Equal(300, result[0].InputTokens);
        Assert.Equal(200, result[1].InputTokens);
        Assert.Equal(100, result[2].InputTokens);
    }

    [Fact]
    public async Task GetRecentUsage_RespectsLimit()
    {
        await SeedAsync(
            CreateUsageEntity(recordedAt: DateTime.UtcNow.AddMinutes(-3)),
            CreateUsageEntity(recordedAt: DateTime.UtcNow.AddMinutes(-2)),
            CreateUsageEntity(recordedAt: DateTime.UtcNow.AddMinutes(-1)),
            CreateUsageEntity(recordedAt: DateTime.UtcNow));

        var result = await _sut.GetRecentUsageAsync(limit: 2);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetRecentUsage_FiltersByRoomId()
    {
        await SeedAsync(
            CreateUsageEntity(roomId: "room-1"),
            CreateUsageEntity(roomId: "room-2"),
            CreateUsageEntity(roomId: "room-1"));

        var result = await _sut.GetRecentUsageAsync(roomId: "room-1");

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("room-1", r.RoomId));
    }

    [Fact]
    public async Task GetRecentUsage_FiltersByAgentId()
    {
        await SeedAsync(
            CreateUsageEntity(agentId: "agent-1"),
            CreateUsageEntity(agentId: "agent-2"),
            CreateUsageEntity(agentId: "agent-1"));

        var result = await _sut.GetRecentUsageAsync(agentId: "agent-1");

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("agent-1", r.AgentId));
    }

    [Fact]
    public async Task GetRecentUsage_FiltersBySince()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateUsageEntity(recordedAt: now.AddHours(-48)),
            CreateUsageEntity(recordedAt: now.AddMinutes(-30)));

        var result = await _sut.GetRecentUsageAsync(since: now.AddHours(-1));

        Assert.Single(result);
    }

    [Fact]
    public async Task GetRecentUsage_CombinedFilters()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            CreateUsageEntity(agentId: "agent-1", roomId: "room-1",
                recordedAt: now.AddMinutes(-10)),
            CreateUsageEntity(agentId: "agent-2", roomId: "room-1",
                recordedAt: now.AddMinutes(-10)),
            CreateUsageEntity(agentId: "agent-1", roomId: "room-2",
                recordedAt: now.AddMinutes(-10)),
            CreateUsageEntity(agentId: "agent-1", roomId: "room-1",
                recordedAt: now.AddHours(-48)));

        var result = await _sut.GetRecentUsageAsync(
            roomId: "room-1", agentId: "agent-1", since: now.AddHours(-1));

        Assert.Single(result);
        Assert.Equal("agent-1", result[0].AgentId);
        Assert.Equal("room-1", result[0].RoomId);
    }

    [Fact]
    public async Task GetRecentUsage_MapsAllFieldsCorrectly()
    {
        var recordedAt = DateTime.UtcNow.AddMinutes(-5);
        await SeedAsync(CreateUsageEntity(
            agentId: "agent-x",
            roomId: "room-x",
            model: "test-model",
            inputTokens: 1000,
            outputTokens: 500,
            cacheReadTokens: 200,
            cacheWriteTokens: 100,
            cost: 0.123,
            durationMs: 999,
            reasoningEffort: "high",
            recordedAt: recordedAt));

        var result = await _sut.GetRecentUsageAsync();

        var r = Assert.Single(result);
        Assert.NotEmpty(r.Id);
        Assert.Equal("agent-x", r.AgentId);
        Assert.Equal("room-x", r.RoomId);
        Assert.Equal("test-model", r.Model);
        Assert.Equal(1000, r.InputTokens);
        Assert.Equal(500, r.OutputTokens);
        Assert.Equal(200, r.CacheReadTokens);
        Assert.Equal(100, r.CacheWriteTokens);
        Assert.Equal(0.123, r.Cost);
        Assert.Equal(999, r.DurationMs);
        Assert.Equal("high", r.ReasoningEffort);
        Assert.Equal(recordedAt, r.RecordedAt);
    }
}
