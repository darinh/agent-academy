using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Aggregates per-agent performance metrics from LLM usage, errors, and tasks.
/// Singleton — uses <see cref="IServiceScopeFactory"/> for DB access.
/// </summary>
public sealed class AgentAnalyticsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentCatalogOptions _catalog;
    private readonly ILogger<AgentAnalyticsService> _logger;

    private const int TrendBuckets = 12;

    public AgentAnalyticsService(
        IServiceScopeFactory scopeFactory,
        AgentCatalogOptions catalog,
        ILogger<AgentAnalyticsService> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<AgentAnalyticsSummary> GetAnalyticsSummaryAsync(
        int? hoursBack,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var since = hoursBack.HasValue ? now.AddHours(-hoursBack.Value) : (DateTime?)null;
        var windowStart = since ?? DateTime.MinValue;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // Build agent name lookup from catalog
        var nameMap = _catalog.Agents
            .ToDictionary(a => a.Id, a => a.Name, StringComparer.OrdinalIgnoreCase);

        // ── Query 1: LLM usage per agent ──
        var usageQuery = db.LlmUsage.AsQueryable();
        if (since.HasValue)
            usageQuery = usageQuery.Where(u => u.RecordedAt >= since.Value);

        var usageByAgent = await usageQuery
            .GroupBy(u => u.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                TotalRequests = g.Count(),
                TotalInput = g.Sum(u => u.InputTokens),
                TotalOutput = g.Sum(u => u.OutputTokens),
                TotalCost = g.Sum(u => (double)(u.Cost ?? 0)),
                AvgDurationMs = g.Average(u => (double?)u.DurationMs),
            })
            .ToListAsync(ct);

        // ── Query 2: Errors per agent ──
        var errorQuery = db.AgentErrors.AsQueryable();
        if (since.HasValue)
            errorQuery = errorQuery.Where(e => e.OccurredAt >= since.Value);

        var errorsByAgent = await errorQuery
            .GroupBy(e => e.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                Total = g.Count(),
                Recoverable = g.Count(e => e.Recoverable),
                Unrecoverable = g.Count(e => !e.Recoverable),
            })
            .ToListAsync(ct);

        // ── Query 3: Tasks per agent ──
        var taskQuery = db.Tasks.Where(t => t.AssignedAgentId != null);
        if (since.HasValue)
            taskQuery = taskQuery.Where(t => t.CreatedAt >= since.Value);

        var tasksByAgent = await taskQuery
            .GroupBy(t => t.AssignedAgentId!)
            .Select(g => new
            {
                AgentId = g.Key,
                Assigned = g.Count(),
                Completed = g.Count(t => t.Status == "Completed"),
            })
            .ToListAsync(ct);

        // ── Query 4: Token trend (raw records for bucketing) ──
        // Cap the trend window to 30 days max to avoid unbounded materialization
        var trendSince = since ?? now.AddDays(-30);
        var trendQuery = db.LlmUsage
            .Where(u => u.RecordedAt >= trendSince);

        var trendRaw = await trendQuery
            .Select(u => new { u.AgentId, u.RecordedAt, Tokens = u.InputTokens + u.OutputTokens })
            .ToListAsync(ct);

        var trendRecords = trendRaw
            .Select(r => new TrendRecord(r.AgentId, r.RecordedAt, r.Tokens))
            .ToList();

        // Build lookup dictionaries
        var errorMap = errorsByAgent.ToDictionary(e => e.AgentId, StringComparer.OrdinalIgnoreCase);
        var taskMap = tasksByAgent.ToDictionary(t => t.AgentId, StringComparer.OrdinalIgnoreCase);

        // Compute window start from all data sources (not just usage)
        var candidates = new List<DateTime>();
        if (trendRecords.Count > 0) candidates.Add(trendRecords.Min(r => r.RecordedAt));
        // For errors/tasks we already have the aggregations; use trendSince as the floor
        if (since.HasValue)
            candidates.Add(since.Value);
        else if (candidates.Count == 0)
            candidates.Add(trendSince);
        var actualStart = candidates.Min();

        var trendByAgent = ComputeTokenTrends(trendRecords, actualStart, now);

        // Collect all agent IDs that appear in any data source
        var allAgentIds = usageByAgent.Select(u => u.AgentId)
            .Union(errorsByAgent.Select(e => e.AgentId))
            .Union(tasksByAgent.Select(t => t.AgentId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var agents = new List<AgentPerformanceMetrics>(allAgentIds.Count);

        foreach (var agentId in allAgentIds)
        {
            var usage = usageByAgent.FirstOrDefault(u =>
                string.Equals(u.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            var errors = errorMap.GetValueOrDefault(agentId);
            var tasks = taskMap.GetValueOrDefault(agentId);
            var trend = trendByAgent.GetValueOrDefault(agentId)
                ?? Enumerable.Repeat(0L, TrendBuckets).ToList();

            agents.Add(new AgentPerformanceMetrics(
                AgentId: agentId,
                AgentName: nameMap.GetValueOrDefault(agentId) ?? agentId,
                TotalRequests: usage?.TotalRequests ?? 0,
                TotalInputTokens: usage?.TotalInput ?? 0,
                TotalOutputTokens: usage?.TotalOutput ?? 0,
                TotalCost: usage?.TotalCost ?? 0,
                AverageResponseTimeMs: usage?.AvgDurationMs,
                TotalErrors: errors?.Total ?? 0,
                RecoverableErrors: errors?.Recoverable ?? 0,
                UnrecoverableErrors: errors?.Unrecoverable ?? 0,
                TasksAssigned: tasks?.Assigned ?? 0,
                TasksCompleted: tasks?.Completed ?? 0,
                TokenTrend: trend));
        }

        // Sort by total requests descending (most active first)
        agents.Sort((a, b) => b.TotalRequests.CompareTo(a.TotalRequests));

        return new AgentAnalyticsSummary(
            Agents: agents,
            WindowStart: new DateTimeOffset(actualStart, TimeSpan.Zero),
            WindowEnd: new DateTimeOffset(now, TimeSpan.Zero),
            TotalRequests: agents.Sum(a => a.TotalRequests),
            TotalCost: agents.Sum(a => a.TotalCost),
            TotalErrors: agents.Sum(a => a.TotalErrors));
    }

    private readonly record struct TrendRecord(string AgentId, DateTime RecordedAt, long Tokens);

    private static Dictionary<string, List<long>> ComputeTokenTrends(
        List<TrendRecord> records,
        DateTime windowStart,
        DateTime windowEnd)
    {
        var result = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        var span = windowEnd - windowStart;
        if (span <= TimeSpan.Zero)
            return result;

        var bucketSize = span.TotalMilliseconds / TrendBuckets;

        foreach (var record in records)
        {
            if (!result.TryGetValue(record.AgentId, out var buckets))
            {
                buckets = Enumerable.Repeat(0L, TrendBuckets).ToList();
                result[record.AgentId] = buckets;
            }

            var offset = (record.RecordedAt - windowStart).TotalMilliseconds;
            var idx = (int)(offset / bucketSize);
            if (idx < 0) idx = 0;
            if (idx >= TrendBuckets) idx = TrendBuckets - 1;
            buckets[idx] += record.Tokens;
        }

        return result;
    }
}
