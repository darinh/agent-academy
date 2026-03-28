using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Activity event endpoints.
/// </summary>
[ApiController]
[Route("api/activity")]
public class ActivityController : ControllerBase
{
    private readonly ActivityBroadcaster _broadcaster;

    public ActivityController(ActivityBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    /// <summary>
    /// GET /api/activity/recent — recent activity events.
    /// </summary>
    [HttpGet("recent")]
    public ActionResult<IReadOnlyList<ActivityEvent>> GetRecentActivity()
    {
        var events = _broadcaster.GetRecentActivity();
        return Ok(events);
    }
}
