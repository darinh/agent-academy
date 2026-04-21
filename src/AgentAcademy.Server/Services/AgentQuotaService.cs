using System.Collections.Concurrent;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Enforces per-agent resource quotas (requests/hour, tokens/hour, cost/hour).
/// Request-rate limiting uses an authoritative in-memory sliding window.
/// Token/cost checks are best-effort via DB aggregation (concurrent calls may
/// slightly overshoot). Singleton; creates its own DB scopes.
/// </summary>
public sealed class AgentQuotaService : IAgentQuotaService
{
    private static readonly TimeSpan QuotaWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan ConfigCacheTtl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly ILogger<AgentQuotaService> _logger;

    // In-memory sliding window for request rate (authoritative)
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _requestWindows = new();

    // Cached quota configs to avoid DB round-trip on every call
    private readonly ConcurrentDictionary<string, (ResourceQuota? Quota, DateTime FetchedAt)> _configCache = new();

    public AgentQuotaService(
        IServiceScopeFactory scopeFactory,
        ILlmUsageTracker usageTracker,
        ILogger<AgentQuotaService> logger)
    {
        _scopeFactory = scopeFactory;
        _usageTracker = usageTracker;
        _logger = logger;
    }

    /// <summary>
    /// Checks whether the agent is allowed to make another LLM call.
    /// If allowed, atomically records the request in the sliding window.
    /// Throws <see cref="AgentQuotaExceededException"/> if denied.
    /// </summary>
    public async Task EnforceQuotaAsync(string agentId)
    {
        var quota = await GetQuotaConfigAsync(agentId);
        if (quota is null)
            return; // No quota configured — unlimited

        if (quota.MaxRequestsPerHour is null && quota.MaxTokensPerHour is null && quota.MaxCostPerHour is null)
            return; // All limits are null — unlimited

        // Check 1: In-memory request rate — atomic check+acquire
        if (quota.MaxRequestsPerHour is not null)
        {
            if (!TryAcquireRequestSlot(agentId, quota.MaxRequestsPerHour.Value, out var retryAfter))
            {
                _logger.LogWarning(
                    "Agent {AgentId} exceeded request quota: {Max} requests/hour. Retry after {Retry}s",
                    agentId, quota.MaxRequestsPerHour.Value, retryAfter);
                throw new AgentQuotaExceededException(
                    agentId, "requests",
                    $"Request limit reached ({quota.MaxRequestsPerHour}/hour). Try again in {retryAfter}s.",
                    retryAfter);
            }
        }

        // Check 2: DB-backed token/cost usage (best-effort)
        if (quota.MaxTokensPerHour is not null || quota.MaxCostPerHour is not null)
        {
            var since = DateTime.UtcNow - QuotaWindow;
            var usage = await _usageTracker.GetAgentUsageSinceAsync(agentId, since);

            if (quota.MaxTokensPerHour is not null && usage.TotalTokens >= quota.MaxTokensPerHour.Value)
            {
                _logger.LogWarning(
                    "Agent {AgentId} exceeded token quota: {Tokens}/{Max} tokens/hour",
                    agentId, usage.TotalTokens, quota.MaxTokensPerHour.Value);
                throw new AgentQuotaExceededException(
                    agentId, "tokens",
                    $"Token limit reached ({usage.TotalTokens:N0}/{quota.MaxTokensPerHour:N0}/hour).",
                    EstimateRetrySeconds());
            }

            if (quota.MaxCostPerHour is not null && usage.TotalCost >= quota.MaxCostPerHour.Value)
            {
                _logger.LogWarning(
                    "Agent {AgentId} exceeded cost quota: ${Cost:F4}/${Max:F4}/hour",
                    agentId, usage.TotalCost, quota.MaxCostPerHour.Value);
                throw new AgentQuotaExceededException(
                    agentId, "cost",
                    $"Cost limit reached (${usage.TotalCost:F4}/${quota.MaxCostPerHour:F4}/hour).",
                    EstimateRetrySeconds());
            }
        }
    }

    /// <summary>
    /// Returns the current quota status for an agent (for API/UI display).
    /// </summary>
    public async Task<QuotaStatus> GetStatusAsync(string agentId)
    {
        var quota = await GetQuotaConfigAsync(agentId);
        if (quota is null || (quota.MaxRequestsPerHour is null && quota.MaxTokensPerHour is null && quota.MaxCostPerHour is null))
        {
            return new QuotaStatus(agentId, true, null, null, quota, null);
        }

        var requestCount = GetRequestCount(agentId);
        var since = DateTime.UtcNow - QuotaWindow;
        var dbUsage = await _usageTracker.GetAgentUsageSinceAsync(agentId, since);

        // Use the higher of in-memory request count and DB count
        var usage = new AgentUsageWindow(
            Math.Max(requestCount, dbUsage.RequestCount),
            dbUsage.TotalTokens,
            dbUsage.TotalCost);

        bool allowed = true;
        string? reason = null;

        if (quota.MaxRequestsPerHour is not null && usage.RequestCount >= quota.MaxRequestsPerHour.Value)
        {
            allowed = false;
            reason = $"Request limit: {usage.RequestCount}/{quota.MaxRequestsPerHour}/hour";
        }
        else if (quota.MaxTokensPerHour is not null && usage.TotalTokens >= quota.MaxTokensPerHour.Value)
        {
            allowed = false;
            reason = $"Token limit: {usage.TotalTokens:N0}/{quota.MaxTokensPerHour:N0}/hour";
        }
        else if (quota.MaxCostPerHour is not null && usage.TotalCost >= quota.MaxCostPerHour.Value)
        {
            allowed = false;
            reason = $"Cost limit: ${usage.TotalCost:F4}/${quota.MaxCostPerHour:F4}/hour";
        }

        return new QuotaStatus(agentId, allowed, reason,
            allowed ? null : GetRetryAfterSeconds(agentId),
            quota, usage);
    }

    /// <summary>
    /// Invalidates the cached quota config for an agent. Call after
    /// quota settings are updated via the API.
    /// </summary>
    public void InvalidateCache(string agentId)
    {
        _configCache.TryRemove(agentId, out _);
    }

    private async Task<ResourceQuota?> GetQuotaConfigAsync(string agentId)
    {
        if (_configCache.TryGetValue(agentId, out var cached) &&
            DateTime.UtcNow - cached.FetchedAt < ConfigCacheTtl)
        {
            return cached.Quota;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var config = await db.AgentConfigs
            .AsNoTracking()
            .Where(c => c.AgentId == agentId)
            .Select(c => new { c.MaxRequestsPerHour, c.MaxTokensPerHour, c.MaxCostPerHour })
            .FirstOrDefaultAsync();

        ResourceQuota? quota = null;
        if (config is not null &&
            (config.MaxRequestsPerHour is not null || config.MaxTokensPerHour is not null || config.MaxCostPerHour is not null))
        {
            quota = new ResourceQuota(config.MaxRequestsPerHour, config.MaxTokensPerHour, config.MaxCostPerHour);
        }

        _configCache[agentId] = (quota, DateTime.UtcNow);
        return quota;
    }

    /// <summary>
    /// Atomically checks the request limit and acquires a slot if allowed.
    /// Returns true if the request is permitted, false if rate-limited.
    /// </summary>
    private bool TryAcquireRequestSlot(string agentId, int maxRequests, out int retryAfterSeconds)
    {
        var now = DateTime.UtcNow;
        var queue = _requestWindows.GetOrAdd(agentId, _ => new Queue<DateTime>());
        lock (queue)
        {
            EvictExpired(queue, now);
            if (queue.Count >= maxRequests)
            {
                var oldest = queue.Peek();
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((QuotaWindow - (now - oldest)).TotalSeconds));
                return false;
            }
            queue.Enqueue(now);
            retryAfterSeconds = 0;
            return true;
        }
    }

    private int GetRequestCount(string agentId)
    {
        if (!_requestWindows.TryGetValue(agentId, out var queue))
            return 0;

        var now = DateTime.UtcNow;
        lock (queue)
        {
            EvictExpired(queue, now);
            return queue.Count;
        }
    }

    private int GetRetryAfterSeconds(string agentId)
    {
        if (!_requestWindows.TryGetValue(agentId, out var queue))
            return EstimateRetrySeconds();

        var now = DateTime.UtcNow;
        lock (queue)
        {
            EvictExpired(queue, now);
            if (queue.Count == 0) return 0;
            var oldest = queue.Peek();
            return Math.Max(1, (int)Math.Ceiling((QuotaWindow - (now - oldest)).TotalSeconds));
        }
    }

    private static void EvictExpired(Queue<DateTime> queue, DateTime now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= QuotaWindow)
            queue.Dequeue();
    }

    /// <summary>
    /// Conservative estimate: the oldest record in the window will expire
    /// somewhere in the next hour. Return 5 minutes as a safe retry period.
    /// </summary>
    private static int EstimateRetrySeconds() => 300;
}
