using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Plan CRUD endpoints scoped to a room.
/// </summary>
[ApiController]
[Route("api/rooms/{roomId}/plan")]
public class PlanController : ControllerBase
{
    private readonly WorkspaceRuntime _runtime;
    private readonly ILogger<PlanController> _logger;

    public PlanController(WorkspaceRuntime runtime, ILogger<PlanController> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/rooms/{roomId}/plan — get the current plan.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PlanContent>> GetPlan(string roomId)
    {
        try
        {
            var plan = await _runtime.GetPlanAsync(roomId);
            if (plan is null)
                return NotFound(new { error = $"No plan found for room '{roomId}'" });

            return Ok(plan);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get plan for room '{RoomId}'", roomId);
            return Problem("Failed to retrieve plan.");
        }
    }

    /// <summary>
    /// PUT /api/rooms/{roomId}/plan — create or update the plan.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> SetPlan(string roomId, [FromBody] PlanContent plan)
    {
        if (string.IsNullOrWhiteSpace(plan.Content))
            return BadRequest(new { error = "Plan content is required" });

        try
        {
            // Verify room or breakout exists before writing plan
            var room = await _runtime.GetRoomAsync(roomId);
            var breakout = room is null ? await _runtime.GetBreakoutRoomAsync(roomId) : null;
            if (room is null && breakout is null)
                return NotFound(new { error = $"Room '{roomId}' not found" });

            await _runtime.SetPlanAsync(roomId, plan.Content);
            return Ok(new { status = "saved", roomId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set plan for room '{RoomId}'", roomId);
            return Problem("Failed to save plan.");
        }
    }

    /// <summary>
    /// DELETE /api/rooms/{roomId}/plan — delete the plan.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> DeletePlan(string roomId)
    {
        try
        {
            var deleted = await _runtime.DeletePlanAsync(roomId);
            if (!deleted)
                return NotFound(new { error = $"No plan found for room '{roomId}'" });

            return Ok(new { status = "deleted", roomId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete plan for room '{RoomId}'", roomId);
            return Problem("Failed to delete plan.");
        }
    }
}
