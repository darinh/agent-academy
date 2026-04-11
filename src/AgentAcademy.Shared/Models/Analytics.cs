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
