namespace AgentAcademy.Shared.Models;

/// <summary>
/// Per-agent performance metrics aggregated over a time window.
/// </summary>
public sealed record AgentPerformanceMetrics(
    string AgentId,
    string AgentName,
    // LLM usage
    int TotalRequests,
    long TotalInputTokens,
    long TotalOutputTokens,
    double TotalCost,
    double? AverageResponseTimeMs,
    // Errors
    int TotalErrors,
    int RecoverableErrors,
    int UnrecoverableErrors,
    // Tasks
    int TasksAssigned,
    int TasksCompleted,
    // Token trend — 12 equal-sized buckets spanning the window, newest last
    List<long> TokenTrend);

/// <summary>
/// All agents' performance plus workspace-level totals.
/// </summary>
public sealed record AgentAnalyticsSummary(
    List<AgentPerformanceMetrics> Agents,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int TotalRequests,
    double TotalCost,
    int TotalErrors);

// ── Drill-down types ──

/// <summary>
/// Detailed analytics for a single agent, including recent activity and breakdowns.
/// </summary>
public sealed record AgentAnalyticsDetail(
    AgentPerformanceMetrics Agent,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    List<AgentUsageRecord> RecentRequests,
    List<AgentErrorRecord> RecentErrors,
    List<AgentTaskRecord> Tasks,
    List<AgentModelBreakdown> ModelBreakdown,
    List<AgentActivityBucket> ActivityBuckets);

public sealed record AgentUsageRecord(
    string Id,
    string? RoomId,
    string? Model,
    long InputTokens,
    long OutputTokens,
    double? Cost,
    double? DurationMs,
    string? ReasoningEffort,
    DateTime RecordedAt);

public sealed record AgentErrorRecord(
    string Id,
    string? RoomId,
    string ErrorType,
    string Message,
    bool Recoverable,
    bool Retried,
    DateTime OccurredAt);

public sealed record AgentTaskRecord(
    string Id,
    string Title,
    string Status,
    string? RoomId,
    string? BranchName,
    string? PullRequestUrl,
    int? PullRequestNumber,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public sealed record AgentModelBreakdown(
    string Model,
    int Requests,
    long TotalTokens,
    double TotalCost);

public sealed record AgentActivityBucket(
    DateTimeOffset BucketStart,
    DateTimeOffset BucketEnd,
    int Requests,
    long Tokens);
