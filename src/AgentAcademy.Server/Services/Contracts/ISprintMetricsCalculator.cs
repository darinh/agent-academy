using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for computing sprint metrics: per-sprint aggregation
/// and workspace-level rollups.
/// </summary>
public interface ISprintMetricsCalculator
{
    /// <summary>
    /// Computes aggregated metrics for a single sprint: duration, stage timing,
    /// task and artifact counts.
    /// </summary>
    Task<SprintMetrics?> GetSprintMetricsAsync(string sprintId);

    /// <summary>
    /// Computes a workspace-level rollup across all sprints: counts, averages
    /// for duration and time per stage.
    /// </summary>
    Task<SprintMetricsSummary> GetMetricsSummaryAsync(string workspacePath);
}
