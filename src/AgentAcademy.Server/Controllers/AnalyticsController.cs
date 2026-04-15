using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Cross-cutting analytics endpoints.
/// </summary>
[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private readonly AgentAnalyticsService _analytics;
    private readonly ITaskAnalyticsService _taskAnalytics;

    public AnalyticsController(AgentAnalyticsService analytics, ITaskAnalyticsService taskAnalytics)
    {
        _analytics = analytics;
        _taskAnalytics = taskAnalytics;
    }

    /// <summary>
    /// Per-agent performance metrics aggregated over a time window.
    /// </summary>
    [HttpGet("agents")]
    public async Task<ActionResult<AgentAnalyticsSummary>> GetAgentAnalytics(
        [FromQuery] int? hoursBack = null,
        CancellationToken ct = default)
    {
        if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
            return BadRequest(ApiProblem.BadRequest("hoursBack must be between 1 and 8760", "invalid_hours_back"));

        var result = await _analytics.GetAnalyticsSummaryAsync(hoursBack, ct);
        return Ok(result);
    }

    /// <summary>
    /// Detailed analytics for a single agent: recent requests, errors, tasks, model breakdown, activity trend.
    /// </summary>
    [HttpGet("agents/{agentId}")]
    public async Task<ActionResult<AgentAnalyticsDetail>> GetAgentDetail(
        string agentId,
        [FromQuery] int? hoursBack = null,
        [FromQuery] int requestLimit = 50,
        [FromQuery] int errorLimit = 20,
        [FromQuery] int taskLimit = 50,
        CancellationToken ct = default)
    {
        if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
            return BadRequest(ApiProblem.BadRequest("hoursBack must be between 1 and 8760", "invalid_hours_back"));

        if (requestLimit < 1 || requestLimit > 200)
            return BadRequest(ApiProblem.BadRequest("requestLimit must be between 1 and 200", "invalid_limit"));
        if (errorLimit < 1 || errorLimit > 200)
            return BadRequest(ApiProblem.BadRequest("errorLimit must be between 1 and 200", "invalid_limit"));
        if (taskLimit < 1 || taskLimit > 200)
            return BadRequest(ApiProblem.BadRequest("taskLimit must be between 1 and 200", "invalid_limit"));

        var result = await _analytics.GetAgentDetailAsync(agentId, hoursBack, requestLimit, errorLimit, taskLimit, ct);
        return Ok(result);
    }

    /// <summary>
    /// Task cycle analytics — effectiveness metrics, cycle times, throughput, and per-agent breakdown.
    /// </summary>
    [HttpGet("tasks")]
    public async Task<ActionResult<TaskCycleAnalytics>> GetTaskAnalytics(
        [FromQuery] int? hoursBack = null,
        CancellationToken ct = default)
    {
        if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
            return BadRequest(ApiProblem.BadRequest("hoursBack must be between 1 and 8760", "invalid_hours_back"));

        var result = await _taskAnalytics.GetTaskCycleAnalyticsAsync(hoursBack, ct);
        return Ok(result);
    }
}
