using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
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
    private readonly PlanService _planService;
    private readonly IRoomService _roomService;
    private readonly IBreakoutRoomService _breakoutRoomService;
    private readonly ILogger<PlanController> _logger;

    public PlanController(
        PlanService planService,
        IRoomService roomService,
        IBreakoutRoomService breakoutRoomService,
        ILogger<PlanController> logger)
    {
        _planService = planService;
        _roomService = roomService;
        _breakoutRoomService = breakoutRoomService;
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
            var plan = await _planService.GetPlanAsync(roomId);
            if (plan is null)
                return NotFound(ApiProblem.NotFound($"No plan found for room '{roomId}'"));

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
            return BadRequest(ApiProblem.BadRequest("Plan content is required"));

        try
        {
            // Verify room or breakout exists before writing plan
            var room = await _roomService.GetRoomAsync(roomId);
            var breakout = room is null ? await _breakoutRoomService.GetBreakoutRoomAsync(roomId) : null;
            if (room is null && breakout is null)
                return NotFound(ApiProblem.NotFound($"Room '{roomId}' not found"));

            await _planService.SetPlanAsync(roomId, plan.Content);
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
            var deleted = await _planService.DeletePlanAsync(roomId);
            if (!deleted)
                return NotFound(ApiProblem.NotFound($"No plan found for room '{roomId}'"));

            return Ok(new { status = "deleted", roomId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete plan for room '{RoomId}'", roomId);
            return Problem("Failed to delete plan.");
        }
    }
}
