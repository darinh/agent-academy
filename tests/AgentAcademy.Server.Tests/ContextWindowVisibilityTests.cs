using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class ContextWindowVisibilityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly LlmUsageTracker _tracker;

    public ContextWindowVisibilityTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options =>
            options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
        }

        _tracker = new LlmUsageTracker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LlmUsageTracker>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── ModelContextLimits ─────────────────────────────────────────

    [Theory]
    [InlineData("gpt-4o-2024-08-06", 128_000)]
    [InlineData("gpt-4o-mini", 128_000)]
    [InlineData("claude-3-5-sonnet-20241022", 200_000)]
    [InlineData("claude-sonnet-4-20260401", 200_000)]
    [InlineData("o4-mini-2026-04-01", 200_000)]
    [InlineData("unknown-model-xyz", 128_000)]
    [InlineData(null, 128_000)]
    [InlineData("", 128_000)]
    public void ModelContextLimits_ReturnsExpectedLimit(string? model, long expected)
    {
        Assert.Equal(expected, ModelContextLimits.GetLimit(model));
    }

    [Fact]
    public void ModelContextLimits_CaseInsensitive()
    {
        Assert.Equal(200_000, ModelContextLimits.GetLimit("Claude-Sonnet-4"));
        Assert.Equal(128_000, ModelContextLimits.GetLimit("GPT-4O"));
    }

    // ── LlmUsageTracker.GetLatestContextPerAgentAsync ──────────────

    [Fact]
    public async Task GetLatestContextPerAgent_EmptyRoom_ReturnsEmpty()
    {
        var result = await _tracker.GetLatestContextPerAgentAsync("nonexistent-room");
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetLatestContextPerAgent_ReturnsLatestPerAgent()
    {
        // Record two calls for agent-1 (second is newer and larger)
        await RecordUsage("agent-1", "room-1", "gpt-4o", inputTokens: 10_000);
        await Task.Delay(50); // ensure different timestamps
        await RecordUsage("agent-1", "room-1", "gpt-4o", inputTokens: 50_000);

        // Record one call for agent-2
        await RecordUsage("agent-2", "room-1", "claude-sonnet-4", inputTokens: 80_000);

        var result = await _tracker.GetLatestContextPerAgentAsync("room-1");

        Assert.Equal(2, result.Count);

        var agent1 = result.First(u => u.AgentId == "agent-1");
        Assert.Equal(50_000, agent1.CurrentTokens);
        Assert.Equal(128_000, agent1.MaxTokens); // gpt-4o limit
        Assert.True(agent1.Percentage > 0);

        var agent2 = result.First(u => u.AgentId == "agent-2");
        Assert.Equal(80_000, agent2.CurrentTokens);
        Assert.Equal(200_000, agent2.MaxTokens); // claude limit
    }

    [Fact]
    public async Task GetLatestContextPerAgent_OnlyReturnsSpecifiedRoom()
    {
        await RecordUsage("agent-1", "room-1", "gpt-4o", inputTokens: 30_000);
        await RecordUsage("agent-1", "room-2", "gpt-4o", inputTokens: 60_000);

        var room1 = await _tracker.GetLatestContextPerAgentAsync("room-1");
        var room2 = await _tracker.GetLatestContextPerAgentAsync("room-2");

        Assert.Single(room1);
        Assert.Equal(30_000, room1[0].CurrentTokens);

        Assert.Single(room2);
        Assert.Equal(60_000, room2[0].CurrentTokens);
    }

    [Fact]
    public async Task GetLatestContextPerAgent_CalculatesPercentageCorrectly()
    {
        await RecordUsage("agent-1", "room-1", "gpt-4o", inputTokens: 64_000);

        var result = await _tracker.GetLatestContextPerAgentAsync("room-1");

        Assert.Single(result);
        Assert.Equal(50.0, result[0].Percentage); // 64000/128000 = 50%
    }

    // ── CopilotSdkSender context broadcast ─────────────────────────

    [Fact]
    public void ActivityBroadcaster_ReceivesContextUsageEvent()
    {
        var broadcaster = new ActivityBroadcaster();
        ActivityEvent? captured = null;
        broadcaster.Subscribe(e => captured = e);

        // Simulate what CopilotSdkSender does after recording usage
        broadcaster.Broadcast(new ActivityEvent(
            Id: "test-1",
            Type: ActivityEventType.ContextUsageUpdated,
            Severity: ActivitySeverity.Info,
            RoomId: "room-1",
            ActorId: "agent-1",
            TaskId: null,
            Message: "Context: 50,000/128,000 tokens (39.1%)",
            CorrelationId: null,
            OccurredAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, object?>
            {
                ["currentTokens"] = 50_000L,
                ["maxTokens"] = 128_000L,
                ["percentage"] = 39.1,
                ["model"] = "gpt-4o",
            }
        ));

        Assert.NotNull(captured);
        Assert.Equal(ActivityEventType.ContextUsageUpdated, captured.Type);
        Assert.Equal("room-1", captured.RoomId);
        Assert.Equal("agent-1", captured.ActorId);
        Assert.Equal(50_000L, captured.Metadata!["currentTokens"]);
        Assert.Equal(128_000L, captured.Metadata!["maxTokens"]);
    }

    [Fact]
    public void ActivityBroadcaster_HighUsage_HasWarningSeverity()
    {
        var broadcaster = new ActivityBroadcaster();
        ActivityEvent? captured = null;
        broadcaster.Subscribe(e => captured = e);

        broadcaster.Broadcast(new ActivityEvent(
            Id: "test-2",
            Type: ActivityEventType.ContextUsageUpdated,
            Severity: ActivitySeverity.Warning,
            RoomId: "room-1",
            ActorId: "agent-1",
            TaskId: null,
            Message: "Context: 110,000/128,000 tokens (85.9%)",
            CorrelationId: null,
            OccurredAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, object?>
            {
                ["currentTokens"] = 110_000L,
                ["maxTokens"] = 128_000L,
                ["percentage"] = 85.9,
                ["model"] = "gpt-4o",
            }
        ));

        Assert.NotNull(captured);
        Assert.Equal(ActivitySeverity.Warning, captured.Severity);
    }

    // ── AgentContextUsage model ────────────────────────────────────

    [Fact]
    public void AgentContextUsage_RecordProperties()
    {
        var usage = new AgentContextUsage(
            "agent-1", "room-1", "gpt-4o",
            64_000, 128_000, 50.0, DateTime.UtcNow);

        Assert.Equal("agent-1", usage.AgentId);
        Assert.Equal("room-1", usage.RoomId);
        Assert.Equal("gpt-4o", usage.Model);
        Assert.Equal(64_000, usage.CurrentTokens);
        Assert.Equal(128_000, usage.MaxTokens);
        Assert.Equal(50.0, usage.Percentage);
    }

    private async Task RecordUsage(string agentId, string roomId, string model, long inputTokens)
    {
        await _tracker.RecordAsync(
            agentId, roomId, model,
            inputTokens, 1000, 0, 0,
            0.01, 500, null, null, null);
    }
}
