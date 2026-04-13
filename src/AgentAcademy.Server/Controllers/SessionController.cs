using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Global conversation session history and statistics.
/// </summary>
[ApiController]
[Route("api/sessions")]
public class SessionController : ControllerBase
{
    private readonly ConversationSessionQueryService _sessionService;
    private readonly ILogger<SessionController> _logger;

    public SessionController(ConversationSessionQueryService sessionService, ILogger<SessionController> logger)
    {
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/sessions — list all conversation sessions across rooms.
    /// Optionally filter by workspace for project-scoped queries.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<SessionListResponse>> GetSessions(
        [FromQuery] string? status = null,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        [FromQuery] int? hoursBack = null,
        [FromQuery] string? workspace = null)
    {
        try
        {
            var (sessions, totalCount) = await _sessionService.GetAllSessionsAsync(
                status, limit, offset, hoursBack, workspace);
            return Ok(new SessionListResponse(sessions, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list sessions");
            return Problem("Failed to retrieve session history.");
        }
    }

    /// <summary>
    /// GET /api/sessions/stats — aggregate session statistics.
    /// Optionally scoped to a workspace.
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<SessionStats>> GetStats(
        [FromQuery] int? hoursBack = null,
        [FromQuery] string? workspace = null)
    {
        try
        {
            var stats = await _sessionService.GetSessionStatsAsync(hoursBack, workspace);
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get session stats");
            return Problem("Failed to retrieve session statistics.");
        }
    }
}
