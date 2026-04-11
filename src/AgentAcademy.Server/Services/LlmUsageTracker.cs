using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Records and queries LLM API usage metrics. Singleton service that
/// creates its own DB scopes for persistence (same pattern as CopilotExecutor).
/// </summary>
public sealed class LlmUsageTracker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LlmUsageTracker> _logger;

    public LlmUsageTracker(
        IServiceScopeFactory scopeFactory,
        ILogger<LlmUsageTracker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Persists a single LLM usage record. Failures are logged but never
    /// propagated — usage tracking must not break agent execution.
    /// </summary>
    public async Task RecordAsync(
        string agentId,
        string? roomId,
        string? model,
        double? inputTokens,
        double? outputTokens,
        double? cacheReadTokens,
        double? cacheWriteTokens,
        double? cost,
        double? durationMs,
        string? apiCallId,
        string? initiator,
        string? reasoningEffort)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            var entity = new LlmUsageEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                AgentId = agentId,
                RoomId = roomId,
                Model = model,
                InputTokens = SafeToLong(inputTokens),
                OutputTokens = SafeToLong(outputTokens),
                CacheReadTokens = SafeToLong(cacheReadTokens),
                CacheWriteTokens = SafeToLong(cacheWriteTokens),
                Cost = double.IsFinite(cost ?? 0) ? cost : null,
                DurationMs = SafeToInt(durationMs),
                ApiCallId = apiCallId,
                Initiator = initiator,
                ReasoningEffort = reasoningEffort,
                RecordedAt = DateTime.UtcNow,
            };

            db.LlmUsage.Add(entity);
            await db.SaveChangesAsync();

            _logger.LogDebug(
                "Recorded LLM usage: agent={AgentId} model={Model} in={Input} out={Output}",
                agentId, model, entity.InputTokens, entity.OutputTokens);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record LLM usage for agent {AgentId} — data lost but agent unaffected",
                agentId);
        }
    }

    /// <summary>
    /// Aggregated usage for a specific agent since a given time.
    /// Used by <see cref="AgentQuotaService"/> for quota enforcement.
    /// </summary>
    public async Task<AgentUsageWindow> GetAgentUsageSinceAsync(string agentId, DateTime since)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var stats = await db.LlmUsage
            .Where(u => u.AgentId == agentId && u.RecordedAt >= since)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalTokens = g.Sum(u => u.InputTokens + u.OutputTokens),
                TotalCost = g.Sum(u => u.Cost ?? 0),
                Count = g.Count(),
            })
            .FirstOrDefaultAsync();

        return stats is null
            ? new AgentUsageWindow(0, 0, 0m)
            : new AgentUsageWindow(stats.Count, stats.TotalTokens, (decimal)stats.TotalCost);
    }

    /// <summary>
    /// Aggregated usage for a specific room.
    /// </summary>
    public async Task<UsageSummary> GetRoomUsageAsync(string roomId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var stats = await db.LlmUsage
            .Where(u => u.RoomId == roomId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalInput = g.Sum(u => u.InputTokens),
                TotalOutput = g.Sum(u => u.OutputTokens),
                TotalCost = g.Sum(u => u.Cost ?? 0),
                Count = g.Count(),
            })
            .FirstOrDefaultAsync();

        var models = await db.LlmUsage
            .Where(u => u.RoomId == roomId && u.Model != null)
            .Select(u => u.Model!)
            .Distinct()
            .ToListAsync();

        if (stats is null)
        {
            return new UsageSummary(0, 0, 0, 0, new List<string>());
        }

        return new UsageSummary(
            TotalInputTokens: stats.TotalInput,
            TotalOutputTokens: stats.TotalOutput,
            TotalCost: stats.TotalCost,
            RequestCount: stats.Count,
            Models: models
        );
    }

    /// <summary>
    /// Per-agent usage breakdown for a room.
    /// </summary>
    public async Task<List<AgentUsageSummary>> GetRoomUsageByAgentAsync(string roomId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        return await db.LlmUsage
            .Where(u => u.RoomId == roomId)
            .GroupBy(u => u.AgentId)
            .Select(g => new AgentUsageSummary(
                g.Key,
                g.Sum(u => u.InputTokens),
                g.Sum(u => u.OutputTokens),
                g.Sum(u => u.Cost ?? 0),
                g.Count()
            ))
            .ToListAsync();
    }

    /// <summary>
    /// Global usage summary across all rooms, optionally filtered by time window.
    /// </summary>
    public async Task<UsageSummary> GetGlobalUsageAsync(DateTime? since = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var query = db.LlmUsage.AsQueryable();
        if (since.HasValue)
            query = query.Where(u => u.RecordedAt >= since.Value);

        var stats = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalInput = g.Sum(u => u.InputTokens),
                TotalOutput = g.Sum(u => u.OutputTokens),
                TotalCost = g.Sum(u => u.Cost ?? 0),
                Count = g.Count(),
            })
            .FirstOrDefaultAsync();

        var models = await query
            .Where(u => u.Model != null)
            .Select(u => u.Model!)
            .Distinct()
            .ToListAsync();

        if (stats is null)
        {
            return new UsageSummary(0, 0, 0, 0, new List<string>());
        }

        return new UsageSummary(
            TotalInputTokens: stats.TotalInput,
            TotalOutputTokens: stats.TotalOutput,
            TotalCost: stats.TotalCost,
            RequestCount: stats.Count,
            Models: models
        );
    }

    /// <summary>
    /// Recent individual usage records (for detailed inspection).
    /// </summary>
    public async Task<List<LlmUsageRecord>> GetRecentUsageAsync(
        string? roomId = null,
        string? agentId = null,
        int limit = 50)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var query = db.LlmUsage.AsQueryable();

        if (roomId is not null)
            query = query.Where(u => u.RoomId == roomId);
        if (agentId is not null)
            query = query.Where(u => u.AgentId == agentId);

        return await query
            .OrderByDescending(u => u.RecordedAt)
            .Take(limit)
            .Select(u => new LlmUsageRecord(
                u.Id,
                u.AgentId,
                u.RoomId,
                u.Model,
                u.InputTokens,
                u.OutputTokens,
                u.CacheReadTokens,
                u.CacheWriteTokens,
                u.Cost,
                u.DurationMs,
                u.ReasoningEffort,
                u.RecordedAt
            ))
            .ToListAsync();
    }

    /// <summary>
    /// Safely converts a nullable double to long, handling NaN/Infinity.
    /// </summary>
    private static long SafeToLong(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
            return 0;
        return (long)Math.Clamp(value.Value, long.MinValue, long.MaxValue);
    }

    /// <summary>
    /// Safely converts a nullable double to int, handling NaN/Infinity.
    /// </summary>
    private static int? SafeToInt(double? value)
    {
        if (!value.HasValue)
            return null;
        if (!double.IsFinite(value.Value))
            return null;
        return (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);
    }
}
