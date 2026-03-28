using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Task submission, human messaging, and phase transitions.
/// </summary>
[ApiController]
public class CollaborationController : ControllerBase
{
    private readonly WorkspaceRuntime _runtime;
    private readonly AgentOrchestrator _orchestrator;
    private readonly ILogger<CollaborationController> _logger;

    public CollaborationController(
        WorkspaceRuntime runtime,
        AgentOrchestrator orchestrator,
        ILogger<CollaborationController> logger)
    {
        _runtime = runtime;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/tasks — submit a new task.
    /// Creates the task via WorkspaceRuntime and kicks off orchestration.
    /// </summary>
    [HttpPost("api/tasks")]
    public async Task<ActionResult<TaskAssignmentResult>> SubmitTask(
        [FromBody] TaskAssignmentRequest request)
    {
        try
        {
            var result = await _runtime.CreateTaskAsync(request);
            _orchestrator.HandleHumanMessage(result.Room.Id);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit task");
            return Problem("Failed to submit task.");
        }
    }

    /// <summary>
    /// POST /api/rooms/{roomId}/human — post a human message.
    /// </summary>
    [HttpPost("api/rooms/{roomId}/human")]
    public async Task<ActionResult<ChatEnvelope>> PostHumanMessage(
        string roomId,
        [FromBody] HumanMessageRequest request)
    {
        try
        {
            var envelope = await _runtime.PostHumanMessageAsync(roomId, request.Content);
            _orchestrator.HandleHumanMessage(roomId);
            return Ok(envelope);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post human message to room '{RoomId}'", roomId);
            return Problem("Failed to post message.");
        }
    }

    /// <summary>
    /// POST /api/rooms/{roomId}/phase — transition room phase.
    /// </summary>
    [HttpPost("api/rooms/{roomId}/phase")]
    public async Task<ActionResult<RoomSnapshot>> TransitionPhase(
        string roomId,
        [FromBody] PhaseTransitionRequest request)
    {
        try
        {
            var snapshot = await _runtime.TransitionPhaseAsync(
                roomId, request.TargetPhase, request.Reason);
            return Ok(snapshot);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transition phase for room '{RoomId}'", roomId);
            return Problem("Failed to transition phase.");
        }
    }
}

/// <summary>
/// Request body for human message endpoint.
/// </summary>
public record HumanMessageRequest(string Content);
