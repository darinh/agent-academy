using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class AgentQuotaServiceTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _connection;
    private readonly AgentQuotaService _quotaService;
    private readonly LlmUsageTracker _usageTracker;

    public AgentQuotaServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _usageTracker = new LlmUsageTracker(scopeFactory, NullLogger<LlmUsageTracker>.Instance);
        _quotaService = new AgentQuotaService(scopeFactory, _usageTracker, NullLogger<AgentQuotaService>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── No quota configured ────────────────────────────────

    [Fact]
    public async Task EnforceQuota_NoConfig_DoesNotThrow()
    {
        await _quotaService.EnforceQuotaAsync("agent-1");
    }

    [Fact]
    public async Task EnforceQuota_AllNullLimits_DoesNotThrow()
    {
        await SetQuota("agent-1", maxRequests: null, maxTokens: null, maxCost: null);
        await _quotaService.EnforceQuotaAsync("agent-1");
    }

    // ── Request rate limiting ──────────────────────────────

    [Fact]
    public async Task EnforceQuota_UnderRequestLimit_Allows()
    {
        await SetQuota("agent-1", maxRequests: 5);

        // Each enforce call atomically checks and acquires a slot
        for (int i = 0; i < 5; i++)
            await _quotaService.EnforceQuotaAsync("agent-1");
    }

    [Fact]
    public async Task EnforceQuota_AtRequestLimit_Throws()
    {
        await SetQuota("agent-1", maxRequests: 3);

        // Use up all 3 slots
        for (int i = 0; i < 3; i++)
            await _quotaService.EnforceQuotaAsync("agent-1");

        // 4th call should throw
        var ex = await Assert.ThrowsAsync<AgentQuotaExceededException>(
            () => _quotaService.EnforceQuotaAsync("agent-1"));

        Assert.Equal("agent-1", ex.AgentId);
        Assert.Equal("requests", ex.QuotaType);
        Assert.True(ex.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task EnforceQuota_DifferentAgents_Independent()
    {
        await SetQuota("agent-1", maxRequests: 2);
        await SetQuota("agent-2", maxRequests: 2);

        // Use up agent-1's slots
        await _quotaService.EnforceQuotaAsync("agent-1");
        await _quotaService.EnforceQuotaAsync("agent-1");

        // Agent 1 should be blocked
        await Assert.ThrowsAsync<AgentQuotaExceededException>(
            () => _quotaService.EnforceQuotaAsync("agent-1"));

        // Agent 2 should be fine
        await _quotaService.EnforceQuotaAsync("agent-2");
    }

    // ── Token quota ────────────────────────────────────────

    [Fact]
    public async Task EnforceQuota_UnderTokenLimit_Allows()
    {
        await SetQuota("agent-1", maxTokens: 10000);

        // Record some usage
        await _usageTracker.RecordAsync("agent-1", "room-1", "gpt-4", 1000, 500, 0, 0, 0.01, 100, null, null, null);

        _quotaService.InvalidateCache("agent-1");
        await _quotaService.EnforceQuotaAsync("agent-1");
    }

    [Fact]
    public async Task EnforceQuota_OverTokenLimit_Throws()
    {
        await SetQuota("agent-1", maxTokens: 1000);

        await _usageTracker.RecordAsync("agent-1", "room-1", "gpt-4", 800, 300, 0, 0, 0.01, 100, null, null, null);

        _quotaService.InvalidateCache("agent-1");
        var ex = await Assert.ThrowsAsync<AgentQuotaExceededException>(
            () => _quotaService.EnforceQuotaAsync("agent-1"));

        Assert.Equal("tokens", ex.QuotaType);
    }

    // ── Cost quota ─────────────────────────────────────────

    [Fact]
    public async Task EnforceQuota_UnderCostLimit_Allows()
    {
        await SetQuota("agent-1", maxCost: 1.0m);

        await _usageTracker.RecordAsync("agent-1", "room-1", "gpt-4", 1000, 500, 0, 0, 0.50, 100, null, null, null);

        _quotaService.InvalidateCache("agent-1");
        await _quotaService.EnforceQuotaAsync("agent-1");
    }

    [Fact]
    public async Task EnforceQuota_OverCostLimit_Throws()
    {
        await SetQuota("agent-1", maxCost: 0.10m);

        await _usageTracker.RecordAsync("agent-1", "room-1", "gpt-4", 1000, 500, 0, 0, 0.50, 100, null, null, null);

        _quotaService.InvalidateCache("agent-1");
        var ex = await Assert.ThrowsAsync<AgentQuotaExceededException>(
            () => _quotaService.EnforceQuotaAsync("agent-1"));

        Assert.Equal("cost", ex.QuotaType);
    }

    // ── Status endpoint ────────────────────────────────────

    [Fact]
    public async Task GetStatus_NoQuota_ReturnsAllowed()
    {
        var status = await _quotaService.GetStatusAsync("agent-1");

        Assert.True(status.IsAllowed);
        Assert.Null(status.DeniedReason);
        Assert.Null(status.ConfiguredQuota);
    }

    [Fact]
    public async Task GetStatus_WithQuota_ShowsUsage()
    {
        await SetQuota("agent-1", maxRequests: 10, maxTokens: 50000);

        // Enforce acquires a slot atomically
        await _quotaService.EnforceQuotaAsync("agent-1");
        await _usageTracker.RecordAsync("agent-1", "room-1", "gpt-4", 1000, 500, 0, 0, 0.01, 100, null, null, null);

        _quotaService.InvalidateCache("agent-1");
        var status = await _quotaService.GetStatusAsync("agent-1");

        Assert.True(status.IsAllowed);
        Assert.NotNull(status.ConfiguredQuota);
        Assert.Equal(10, status.ConfiguredQuota!.MaxRequestsPerHour);
        Assert.NotNull(status.CurrentUsage);
        Assert.True(status.CurrentUsage!.RequestCount >= 1);
        Assert.Equal(1500, status.CurrentUsage.TotalTokens);
    }

    [Fact]
    public async Task GetStatus_OverLimit_ReturnsDenied()
    {
        await SetQuota("agent-1", maxRequests: 2);

        // Fill up the quota
        await _quotaService.EnforceQuotaAsync("agent-1");
        await _quotaService.EnforceQuotaAsync("agent-1");

        var status = await _quotaService.GetStatusAsync("agent-1");

        Assert.False(status.IsAllowed);
        Assert.NotNull(status.DeniedReason);
        Assert.Contains("Request limit", status.DeniedReason);
    }

    // ── Cache invalidation ─────────────────────────────────

    [Fact]
    public async Task InvalidateCache_UpdatesQuotaConfig()
    {
        await SetQuota("agent-1", maxRequests: 2);

        // Fill up the quota
        await _quotaService.EnforceQuotaAsync("agent-1");
        await _quotaService.EnforceQuotaAsync("agent-1");

        // Should be blocked
        await Assert.ThrowsAsync<AgentQuotaExceededException>(
            () => _quotaService.EnforceQuotaAsync("agent-1"));

        // Increase the limit
        await SetQuota("agent-1", maxRequests: 10);
        _quotaService.InvalidateCache("agent-1");

        // Should now be allowed
        await _quotaService.EnforceQuotaAsync("agent-1");
    }

    // ── Usage tracker aggregation ──────────────────────────

    [Fact]
    public async Task GetAgentUsageSince_AggregatesCorrectly()
    {
        await _usageTracker.RecordAsync("agent-1", "room-1", "gpt-4", 1000, 500, 0, 0, 0.10, 100, null, null, null);
        await _usageTracker.RecordAsync("agent-1", "room-2", "gpt-4", 2000, 800, 0, 0, 0.20, 150, null, null, null);
        await _usageTracker.RecordAsync("agent-2", "room-1", "gpt-4", 5000, 1000, 0, 0, 0.50, 200, null, null, null);

        var since = DateTime.UtcNow.AddHours(-1);
        var usage = await _usageTracker.GetAgentUsageSinceAsync("agent-1", since);

        Assert.Equal(2, usage.RequestCount);
        Assert.Equal(4300, usage.TotalTokens); // (1000+500) + (2000+800)
        Assert.Equal(0.30m, usage.TotalCost);
    }

    [Fact]
    public async Task GetAgentUsageSince_ExcludesOldRecords()
    {
        // Insert a record and then check with a future "since"
        await _usageTracker.RecordAsync("agent-1", "room-1", "gpt-4", 1000, 500, 0, 0, 0.10, 100, null, null, null);

        var since = DateTime.UtcNow.AddSeconds(10); // Future — should exclude all records
        var usage = await _usageTracker.GetAgentUsageSinceAsync("agent-1", since);

        Assert.Equal(0, usage.RequestCount);
        Assert.Equal(0, usage.TotalTokens);
        Assert.Equal(0m, usage.TotalCost);
    }

    // ── Combined quotas ────────────────────────────────────

    [Fact]
    public async Task EnforceQuota_RequestAllowed_TokenBlocked()
    {
        await SetQuota("agent-1", maxRequests: 100, maxTokens: 500);

        await _usageTracker.RecordAsync("agent-1", "room-1", "gpt-4", 400, 200, 0, 0, 0.01, 100, null, null, null);

        _quotaService.InvalidateCache("agent-1");
        var ex = await Assert.ThrowsAsync<AgentQuotaExceededException>(
            () => _quotaService.EnforceQuotaAsync("agent-1"));

        Assert.Equal("tokens", ex.QuotaType);
    }

    // ── Helpers ─────────────────────────────────────────────

    private async Task SetQuota(string agentId, int? maxRequests = null, long? maxTokens = null, decimal? maxCost = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var config = await db.AgentConfigs.FindAsync(agentId);
        if (config is null)
        {
            config = new AgentConfigEntity
            {
                AgentId = agentId,
                MaxRequestsPerHour = maxRequests,
                MaxTokensPerHour = maxTokens,
                MaxCostPerHour = maxCost,
                UpdatedAt = DateTime.UtcNow,
            };
            db.AgentConfigs.Add(config);
        }
        else
        {
            config.MaxRequestsPerHour = maxRequests;
            config.MaxTokensPerHour = maxTokens;
            config.MaxCostPerHour = maxCost;
            config.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }
}
