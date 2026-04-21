using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Computes task cycle analytics — effectiveness metrics derived from task
/// lifecycle data. Scoped service (one DbContext per request).
/// </summary>
public sealed class TaskAnalyticsService : ITaskAnalyticsService
{
    private readonly AgentAcademyDbContext _db;
    private readonly IAgentCatalog _catalog;

    private const int ThroughputBuckets = 12;

    public TaskAnalyticsService(
        AgentAcademyDbContext db,
        IAgentCatalog catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<TaskCycleAnalytics> GetTaskCycleAnalyticsAsync(
        int? hoursBack,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var since = hoursBack.HasValue ? now.AddHours(-hoursBack.Value) : (DateTime?)null;
        var windowStart = since ?? DateTime.MinValue;

        var nameMap = _catalog.Agents
            .ToDictionary(a => a.Id, a => a.Name, StringComparer.OrdinalIgnoreCase);

        // ── Query 1: Status snapshot (all tasks, not time-windowed) ──
        var allStatuses = await _db.Tasks
            .AsNoTracking()
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var statusLookup = allStatuses.ToDictionary(s => s.Status, s => s.Count, StringComparer.OrdinalIgnoreCase);
        int S(string key) => statusLookup.GetValueOrDefault(key, 0);

        var statusCounts = new TaskStatusCounts(
            Queued: S("Queued"),
            Active: S("Active"),
            Blocked: S("Blocked"),
            AwaitingValidation: S("AwaitingValidation"),
            InReview: S("InReview"),
            ChangesRequested: S("ChangesRequested"),
            Approved: S("Approved"),
            Merging: S("Merging"),
            Completed: S("Completed"),
            Cancelled: S("Cancelled"));

        // ── Query 2: Completed tasks in window (for cycle time metrics) ──
        var completedQuery = _db.Tasks.AsNoTracking()
            .Where(t => t.Status == "Completed" && t.CompletedAt != null);
        if (since.HasValue)
            completedQuery = completedQuery.Where(t => t.CompletedAt >= since.Value);

        var completedTasks = await completedQuery
            .Select(t => new
            {
                t.Id,
                t.AssignedAgentId,
                t.CreatedAt,
                t.StartedAt,
                t.CompletedAt,
                t.ReviewRounds,
                t.CommitCount,
            })
            .ToListAsync(ct);

        // ── Query 3: All tasks created in window (for creation counts + per-agent totals) ──
        var createdQuery = _db.Tasks.AsNoTracking();
        if (since.HasValue)
            createdQuery = createdQuery.Where(t => t.CreatedAt >= since.Value);

        var createdTasks = await createdQuery
            .Select(t => new
            {
                t.Id,
                t.AssignedAgentId,
                t.Status,
                t.Type,
                t.ReviewRounds,
            })
            .ToListAsync(ct);

        // ── Query 4: Throughput buckets (completed tasks over time) ──
        // Also need creation timestamps for the throughput chart
        var throughputCompletedQuery = _db.Tasks.AsNoTracking()
            .Where(t => t.CompletedAt != null);
        if (since.HasValue)
            throughputCompletedQuery = throughputCompletedQuery.Where(t => t.CompletedAt >= since.Value);

        var throughputCompleted = await throughputCompletedQuery
            .Select(t => new { t.CompletedAt })
            .ToListAsync(ct);

        var throughputCreated = await createdQuery
            .Select(t => new { t.CreatedAt })
            .ToListAsync(ct);

        // ── Compute overall metrics ──
        // Use the union of created-in-window and completed-in-window task IDs as the
        // denominator to avoid completion rate exceeding 100% (tasks created before
        // the window but completed inside it would otherwise inflate the numerator).
        var createdIds = new HashSet<string>(
            createdTasks.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        var completedIds = new HashSet<string>(
            completedTasks.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);

        int totalTasks = since.HasValue
            ? createdIds.Union(completedIds).Count()
            : allStatuses.Sum(s => s.Count);

        int totalCompleted = completedTasks.Count;
        double completionRate = totalTasks > 0 ? (double)totalCompleted / totalTasks : 0;

        var cycleTimesHours = completedTasks
            .Select(t => (t.CompletedAt!.Value - t.CreatedAt).TotalHours)
            .ToList();

        var queueTimesHours = completedTasks
            .Where(t => t.StartedAt.HasValue)
            .Select(t => (t.StartedAt!.Value - t.CreatedAt).TotalHours)
            .ToList();

        var executionSpansHours = completedTasks
            .Where(t => t.StartedAt.HasValue)
            .Select(t => (t.CompletedAt!.Value - t.StartedAt!.Value).TotalHours)
            .ToList();

        var reviewRounds = completedTasks
            .Where(t => t.ReviewRounds > 0)
            .Select(t => (double)t.ReviewRounds)
            .ToList();

        int totalCommits = completedTasks.Sum(t => t.CommitCount);

        // Rework rate: tasks that went through more than 1 review round
        int reworked = completedTasks.Count(t => t.ReviewRounds > 1);
        double reworkRate = totalCompleted > 0 ? (double)reworked / totalCompleted : 0;

        var overview = new TaskCycleOverview(
            TotalTasks: totalTasks,
            StatusCounts: statusCounts,
            CompletionRate: Math.Round(completionRate, 4),
            AvgCycleTimeHours: SafeAvg(cycleTimesHours),
            AvgQueueTimeHours: SafeAvg(queueTimesHours),
            AvgExecutionSpanHours: SafeAvg(executionSpansHours),
            AvgReviewRounds: SafeAvg(reviewRounds),
            ReworkRate: Math.Round(reworkRate, 4),
            TotalCommits: totalCommits);

        // ── Compute per-agent effectiveness ──
        var agentIds = createdTasks
            .Where(t => t.AssignedAgentId != null)
            .Select(t => t.AssignedAgentId!)
            .Concat(completedTasks
                .Where(t => t.AssignedAgentId != null)
                .Select(t => t.AssignedAgentId!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var agentEffectiveness = new List<AgentTaskEffectiveness>();

        foreach (var agentId in agentIds)
        {
            var agentCreated = createdTasks
                .Where(t => string.Equals(t.AssignedAgentId, agentId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var agentCompleted = completedTasks
                .Where(t => string.Equals(t.AssignedAgentId, agentId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int assigned = agentCreated.Count;
            int completed = agentCompleted.Count;
            int cancelled = agentCreated.Count(t => t.Status == "Cancelled");

            // Union of created + completed IDs to avoid rate > 100%
            var agentCreatedIds = new HashSet<string>(
                agentCreated.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            var agentCompletedIds = new HashSet<string>(
                agentCompleted.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            int agentTotal = since.HasValue
                ? agentCreatedIds.Union(agentCompletedIds).Count()
                : assigned;

            double agentCompletionRate = agentTotal > 0 ? (double)completed / agentTotal : 0;

            var agentCycleTimes = agentCompleted
                .Select(t => (t.CompletedAt!.Value - t.CreatedAt).TotalHours)
                .ToList();

            var agentQueueTimes = agentCompleted
                .Where(t => t.StartedAt.HasValue)
                .Select(t => (t.StartedAt!.Value - t.CreatedAt).TotalHours)
                .ToList();

            var agentExecSpans = agentCompleted
                .Where(t => t.StartedAt.HasValue)
                .Select(t => (t.CompletedAt!.Value - t.StartedAt!.Value).TotalHours)
                .ToList();

            var agentReviewRounds = agentCompleted
                .Where(t => t.ReviewRounds > 0)
                .Select(t => (double)t.ReviewRounds)
                .ToList();

            var agentCommits = agentCompleted
                .Where(t => t.CommitCount > 0)
                .Select(t => (double)t.CommitCount)
                .ToList();

            int firstPass = agentCompleted.Count(t => t.ReviewRounds <= 1);
            double firstPassRate = completed > 0 ? (double)firstPass / completed : 0;

            int agentReworked = agentCompleted.Count(t => t.ReviewRounds > 1);
            double agentReworkRate = completed > 0 ? (double)agentReworked / completed : 0;

            agentEffectiveness.Add(new AgentTaskEffectiveness(
                AgentId: agentId,
                AgentName: nameMap.GetValueOrDefault(agentId, agentId),
                Assigned: assigned,
                Completed: completed,
                Cancelled: cancelled,
                CompletionRate: Math.Round(agentCompletionRate, 4),
                AvgCycleTimeHours: SafeAvg(agentCycleTimes),
                AvgQueueTimeHours: SafeAvg(agentQueueTimes),
                AvgExecutionSpanHours: SafeAvg(agentExecSpans),
                AvgReviewRounds: SafeAvg(agentReviewRounds),
                AvgCommitsPerTask: SafeAvg(agentCommits),
                FirstPassApprovalRate: Math.Round(firstPassRate, 4),
                ReworkRate: Math.Round(agentReworkRate, 4)));
        }

        // ── Compute throughput buckets ──
        var bucketStart = since ?? (throughputCompleted.Any()
            ? throughputCompleted.Min(t => t.CompletedAt!.Value)
            : now.AddDays(-7));
        var bucketEnd = now;
        var bucketSpan = (bucketEnd - bucketStart) / ThroughputBuckets;
        if (bucketSpan <= TimeSpan.Zero)
            bucketSpan = TimeSpan.FromHours(1);

        var throughputBuckets = new List<TaskCycleBucket>();
        for (int i = 0; i < ThroughputBuckets; i++)
        {
            var bStart = bucketStart.AddTicks(bucketSpan.Ticks * i);
            var bEnd = i == ThroughputBuckets - 1 ? bucketEnd : bucketStart.AddTicks(bucketSpan.Ticks * (i + 1));

            int completedInBucket = throughputCompleted
                .Count(t => t.CompletedAt!.Value >= bStart && t.CompletedAt.Value < bEnd);

            int createdInBucket = throughputCreated
                .Count(t => t.CreatedAt >= bStart && t.CreatedAt < bEnd);

            throughputBuckets.Add(new TaskCycleBucket(
                BucketStart: new DateTimeOffset(bStart, TimeSpan.Zero),
                BucketEnd: new DateTimeOffset(bEnd, TimeSpan.Zero),
                Completed: completedInBucket,
                Created: createdInBucket));
        }

        // ── Type breakdown (from tasks created in window) ──
        var typeBreakdown = new TaskTypeBreakdown(
            Feature: createdTasks.Count(t => t.Type == "Feature"),
            Bug: createdTasks.Count(t => t.Type == "Bug"),
            Chore: createdTasks.Count(t => t.Type == "Chore"),
            Spike: createdTasks.Count(t => t.Type == "Spike"));

        return new TaskCycleAnalytics(
            Overview: overview,
            AgentEffectiveness: agentEffectiveness.OrderByDescending(a => a.Completed).ToList(),
            ThroughputBuckets: throughputBuckets,
            TypeBreakdown: typeBreakdown,
            WindowStart: new DateTimeOffset(windowStart, TimeSpan.Zero),
            WindowEnd: new DateTimeOffset(now, TimeSpan.Zero));
    }

    private static double? SafeAvg(List<double> values)
        => values.Count > 0 ? Math.Round(values.Average(), 2) : null;
}
