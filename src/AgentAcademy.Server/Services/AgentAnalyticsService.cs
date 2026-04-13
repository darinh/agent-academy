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
    private readonly IAgentCatalog _catalog;
    private readonly ILogger<AgentAnalyticsService> _logger;

    private const int TrendBuckets = 12;

    public AgentAnalyticsService(
        IServiceScopeFactory scopeFactory,
        IAgentCatalog catalog,
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

    private const int DetailBuckets = 24;

    /// <summary>
    /// Detailed analytics for a single agent — usage records, errors, tasks, model breakdown, activity trend.
    /// </summary>
    public async Task<AgentAnalyticsDetail> GetAgentDetailAsync(
        string agentId,
        int? hoursBack,
        int requestLimit = 50,
        int errorLimit = 20,
        int taskLimit = 50,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var since = hoursBack.HasValue ? now.AddHours(-hoursBack.Value) : (DateTime?)null;

        // Resolve agent name from catalog; allow non-catalog agents (from telemetry)
        var agentDef = _catalog.Agents
            .FirstOrDefault(a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));
        var agentName = agentDef?.Name ?? agentId;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // Use EF.Functions.Collate for case-insensitive matching on agent ID
        var normalizedId = agentId;

        // ── Query 1: Aggregate metrics (agent-scoped) ──
        var usageQuery = db.LlmUsage.Where(u => u.AgentId == normalizedId);
        if (since.HasValue)
            usageQuery = usageQuery.Where(u => u.RecordedAt >= since.Value);

        var usageAgg = await usageQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalRequests = g.Count(),
                TotalInput = g.Sum(u => u.InputTokens),
                TotalOutput = g.Sum(u => u.OutputTokens),
                TotalCost = g.Sum(u => (double)(u.Cost ?? 0)),
                AvgDurationMs = g.Average(u => (double?)u.DurationMs),
            })
            .FirstOrDefaultAsync(ct);

        var errorQuery = db.AgentErrors.Where(e => e.AgentId == agentId);
        if (since.HasValue)
            errorQuery = errorQuery.Where(e => e.OccurredAt >= since.Value);

        var errorAgg = await errorQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Recoverable = g.Count(e => e.Recoverable),
                Unrecoverable = g.Count(e => !e.Recoverable),
            })
            .FirstOrDefaultAsync(ct);

        // Tasks active in window: created OR completed within the range
        var taskQuery = db.Tasks.Where(t => t.AssignedAgentId == agentId);
        if (since.HasValue)
            taskQuery = taskQuery.Where(t => t.CreatedAt >= since.Value || t.CompletedAt >= since.Value);

        var taskAgg = await taskQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Assigned = g.Count(),
                Completed = g.Count(t => t.Status == "Completed"),
            })
            .FirstOrDefaultAsync(ct);

        // ── Token trend (12 buckets, matching summary) ──
        var trendSince = since ?? now.AddDays(-30);
        var trendRaw = await db.LlmUsage
            .Where(u => u.AgentId == agentId && u.RecordedAt >= trendSince)
            .Select(u => new { u.RecordedAt, Tokens = u.InputTokens + u.OutputTokens })
            .ToListAsync(ct);

        var trendRecords = trendRaw
            .Select(r => new TrendRecord(agentId, r.RecordedAt, r.Tokens))
            .ToList();

        var windowStart = since ?? (trendRecords.Count > 0 ? trendRecords.Min(r => r.RecordedAt) : trendSince);
        var tokenTrends = ComputeTokenTrends(trendRecords, windowStart, now);
        var tokenTrend = tokenTrends.GetValueOrDefault(agentId)
            ?? Enumerable.Repeat(0L, TrendBuckets).ToList();

        var metrics = new AgentPerformanceMetrics(
            AgentId: agentId,
            AgentName: agentName,
            TotalRequests: usageAgg?.TotalRequests ?? 0,
            TotalInputTokens: usageAgg?.TotalInput ?? 0,
            TotalOutputTokens: usageAgg?.TotalOutput ?? 0,
            TotalCost: usageAgg?.TotalCost ?? 0,
            AverageResponseTimeMs: usageAgg?.AvgDurationMs,
            TotalErrors: errorAgg?.Total ?? 0,
            RecoverableErrors: errorAgg?.Recoverable ?? 0,
            UnrecoverableErrors: errorAgg?.Unrecoverable ?? 0,
            TasksAssigned: taskAgg?.Assigned ?? 0,
            TasksCompleted: taskAgg?.Completed ?? 0,
            TokenTrend: tokenTrend);

        // ── Query 2: Recent requests ──
        var recentUsageQuery = db.LlmUsage
            .Where(u => u.AgentId == agentId);
        if (since.HasValue)
            recentUsageQuery = recentUsageQuery.Where(u => u.RecordedAt >= since.Value);

        var recentRequests = await recentUsageQuery
            .OrderByDescending(u => u.RecordedAt)
            .Take(requestLimit)
            .Select(u => new AgentUsageRecord(
                u.Id, u.RoomId, u.Model,
                u.InputTokens, u.OutputTokens,
                (double?)u.Cost, (double?)u.DurationMs,
                u.ReasoningEffort, u.RecordedAt))
            .ToListAsync(ct);

        // ── Query 3: Recent errors ──
        var recentErrorQuery = db.AgentErrors
            .Where(e => e.AgentId == agentId);
        if (since.HasValue)
            recentErrorQuery = recentErrorQuery.Where(e => e.OccurredAt >= since.Value);

        var recentErrors = await recentErrorQuery
            .OrderByDescending(e => e.OccurredAt)
            .Take(errorLimit)
            .Select(e => new AgentErrorRecord(
                e.Id, e.RoomId, e.ErrorType, e.Message,
                e.Recoverable, e.Retried, e.OccurredAt))
            .ToListAsync(ct);

        // ── Query 4: Tasks (capped) ──
        var recentTaskQuery = db.Tasks
            .Where(t => t.AssignedAgentId == agentId);
        if (since.HasValue)
            recentTaskQuery = recentTaskQuery.Where(t => t.CreatedAt >= since.Value || t.CompletedAt >= since.Value);

        var tasks = await recentTaskQuery
            .OrderByDescending(t => t.CreatedAt)
            .Take(taskLimit)
            .Select(t => new AgentTaskRecord(
                t.Id, t.Title, t.Status, t.RoomId,
                t.BranchName, t.PullRequestUrl, t.PullRequestNumber,
                t.CreatedAt, t.CompletedAt))
            .ToListAsync(ct);

        // ── Query 5: Model breakdown ──
        var modelQuery = db.LlmUsage
            .Where(u => u.AgentId == agentId);
        if (since.HasValue)
            modelQuery = modelQuery.Where(u => u.RecordedAt >= since.Value);

        var modelRaw = await modelQuery
            .GroupBy(u => u.Model ?? "unknown")
            .Select(g => new
            {
                Model = g.Key,
                Requests = g.Count(),
                TotalTokens = g.Sum(u => u.InputTokens + u.OutputTokens),
                TotalCost = g.Sum(u => (double)(u.Cost ?? 0)),
            })
            .OrderByDescending(m => m.Requests)
            .ToListAsync(ct);

        var modelBreakdown = modelRaw
            .Select(m => new AgentModelBreakdown(m.Model, m.Requests, m.TotalTokens, m.TotalCost))
            .ToList();

        // ── Query 6: Activity buckets (24 fixed buckets) ──
        var activityBuckets = ComputeActivityBuckets(
            trendRaw.Select(r => (r.RecordedAt, r.Tokens)).ToList(),
            windowStart, now, DetailBuckets);

        return new AgentAnalyticsDetail(
            Agent: metrics,
            WindowStart: new DateTimeOffset(windowStart, TimeSpan.Zero),
            WindowEnd: new DateTimeOffset(now, TimeSpan.Zero),
            RecentRequests: recentRequests,
            RecentErrors: recentErrors,
            Tasks: tasks,
            ModelBreakdown: modelBreakdown,
            ActivityBuckets: activityBuckets);
    }

    private static List<AgentActivityBucket> ComputeActivityBuckets(
        List<(DateTime RecordedAt, long Tokens)> records,
        DateTime windowStart,
        DateTime windowEnd,
        int bucketCount)
    {
        var span = windowEnd - windowStart;
        var buckets = new List<AgentActivityBucket>(bucketCount);

        if (span <= TimeSpan.Zero)
        {
            for (var i = 0; i < bucketCount; i++)
                buckets.Add(new AgentActivityBucket(
                    new DateTimeOffset(windowStart, TimeSpan.Zero),
                    new DateTimeOffset(windowEnd, TimeSpan.Zero), 0, 0));
            return buckets;
        }

        var bucketMs = span.TotalMilliseconds / bucketCount;

        // Initialize buckets with start/end times
        var requestCounts = new int[bucketCount];
        var tokenCounts = new long[bucketCount];

        foreach (var record in records)
        {
            var offset = (record.RecordedAt - windowStart).TotalMilliseconds;
            var idx = (int)(offset / bucketMs);
            if (idx < 0) idx = 0;
            if (idx >= bucketCount) idx = bucketCount - 1;
            requestCounts[idx]++;
            tokenCounts[idx] += record.Tokens;
        }

        for (var i = 0; i < bucketCount; i++)
        {
            var start = windowStart.AddMilliseconds(i * bucketMs);
            var end = i == bucketCount - 1 ? windowEnd : windowStart.AddMilliseconds((i + 1) * bucketMs);
            buckets.Add(new AgentActivityBucket(
                new DateTimeOffset(start, TimeSpan.Zero),
                new DateTimeOffset(end, TimeSpan.Zero),
                requestCounts[i],
                tokenCounts[i]));
        }

        return buckets;
    }

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
