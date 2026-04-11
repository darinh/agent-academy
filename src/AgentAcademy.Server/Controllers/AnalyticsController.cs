using AgentAcademy.Server.Services;
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

    public AnalyticsController(AgentAnalyticsService analytics)
    {
        _analytics = analytics;
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
            return BadRequest(new { code = "invalid_hours_back", message = "hoursBack must be between 1 and 8760" });

        var result = await _analytics.GetAnalyticsSummaryAsync(hoursBack, ct);
        return Ok(result);
    }
}
