namespace AgentAcademy.Shared.Models;

/// <summary>
/// Task cycle analytics — effectiveness metrics derived from task lifecycle data.
/// Covers completion rates, cycle times, review effort, and throughput.
/// </summary>
public sealed record TaskCycleAnalytics(
    TaskCycleOverview Overview,
    List<AgentTaskEffectiveness> AgentEffectiveness,
    List<TaskCycleBucket> ThroughputBuckets,
    TaskTypeBreakdown TypeBreakdown,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd);

/// <summary>
/// Workspace-level task cycle overview.
/// </summary>
public sealed record TaskCycleOverview(
    int TotalTasks,
    TaskStatusCounts StatusCounts,
    double CompletionRate,
    double? AvgCycleTimeHours,
    double? AvgQueueTimeHours,
    double? AvgExecutionSpanHours,
    double? AvgReviewRounds,
    double ReworkRate,
    int TotalCommits);

/// <summary>
/// Task counts by status.
/// </summary>
public sealed record TaskStatusCounts(
    int Queued,
    int Active,
    int Blocked,
    int AwaitingValidation,
    int InReview,
    int ChangesRequested,
    int Approved,
    int Merging,
    int Completed,
    int Cancelled);

/// <summary>
/// Per-agent task effectiveness metrics. Attribution is based on the current
/// assignee at query time — reassigned tasks credit only the final assignee.
/// </summary>
public sealed record AgentTaskEffectiveness(
    string AgentId,
    string AgentName,
    int Assigned,
    int Completed,
    int Cancelled,
    double CompletionRate,
    double? AvgCycleTimeHours,
    double? AvgQueueTimeHours,
    double? AvgExecutionSpanHours,
    double? AvgReviewRounds,
    double? AvgCommitsPerTask,
    double FirstPassApprovalRate,
    double ReworkRate);

/// <summary>
/// Time-series bucket for task throughput visualization.
/// </summary>
public sealed record TaskCycleBucket(
    DateTimeOffset BucketStart,
    DateTimeOffset BucketEnd,
    int Completed,
    int Created);

/// <summary>
/// Task counts grouped by type.
/// </summary>
public sealed record TaskTypeBreakdown(
    int Feature,
    int Bug,
    int Chore,
    int Spike);
