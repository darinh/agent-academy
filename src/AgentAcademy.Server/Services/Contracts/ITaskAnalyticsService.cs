using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Computes task effectiveness analytics from task lifecycle data.
/// All methods are read-only — no mutations.
/// </summary>
public interface ITaskAnalyticsService
{
    /// <summary>
    /// Computes task cycle analytics over a configurable time window.
    /// Returns overview metrics, per-agent effectiveness, throughput buckets,
    /// and type breakdowns.
    /// </summary>
    /// <param name="hoursBack">
    /// Number of hours to look back. Null uses the service default.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<TaskCycleAnalytics> GetTaskCycleAnalyticsAsync(int? hoursBack, CancellationToken ct = default);
}
