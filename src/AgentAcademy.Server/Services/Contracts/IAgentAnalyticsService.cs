using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for aggregating per-agent performance metrics.
/// </summary>
public interface IAgentAnalyticsService
{
    /// <summary>
    /// Aggregates analytics across all agents for the given time window.
    /// </summary>
    Task<AgentAnalyticsSummary> GetAnalyticsSummaryAsync(
        int? hoursBack,
        CancellationToken ct = default);

    /// <summary>
    /// Detailed analytics for a single agent — usage records, errors, tasks,
    /// model breakdown, and activity trend.
    /// </summary>
    Task<AgentAnalyticsDetail> GetAgentDetailAsync(
        string agentId,
        int? hoursBack,
        int requestLimit = 50,
        int errorLimit = 20,
        int taskLimit = 50,
        CancellationToken ct = default);
}
