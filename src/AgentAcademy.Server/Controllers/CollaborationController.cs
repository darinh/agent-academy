using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Task submission, messaging, human input, phase transitions, and session compaction.
/// </summary>
[ApiController]
public class CollaborationController : ControllerBase
{
    private readonly WorkspaceRuntime _runtime;
    private readonly AgentOrchestrator _orchestrator;
    private readonly IAgentExecutor _executor;
    private readonly ILogger<CollaborationController> _logger;

    public CollaborationController(
        WorkspaceRuntime runtime,
        AgentOrchestrator orchestrator,
        IAgentExecutor executor,
        ILogger<CollaborationController> logger)
    {
        _runtime = runtime;
        _orchestrator = orchestrator;
        _executor = executor;
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
        if (request is null)
            return BadRequest(new { code = "invalid_task_request", message = "Task payload is required." });

        try
        {
            var result = await _runtime.CreateTaskAsync(request);
            _orchestrator.HandleHumanMessage(result.Room.Id);
            return StatusCode(201, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_task_request", message = ex.Message });
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
    /// POST /api/rooms/{roomId}/messages — post a message (agent-to-agent or system).
    /// </summary>
    [HttpPost("api/rooms/{roomId}/messages")]
    public async Task<ActionResult<ChatEnvelope>> PostMessage(
        string roomId,
        [FromBody] PostMessageRequest request)
    {
        if (request is null)
            return BadRequest(new { code = "invalid_message", message = "Message payload is required." });

        try
        {
            // Override roomId from path, matching v1 behavior
            var adjusted = request with { RoomId = roomId };
            var envelope = await _runtime.PostMessageAsync(adjusted);
            return Ok(envelope);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_message", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post message to room '{RoomId}'", roomId);
            return Problem("Failed to post message.");
        }
    }

    /// <summary>
    /// POST /api/rooms/{roomId}/human — post a human message.
    /// Triggers orchestration after posting. Rate limiting is enforced
    /// via middleware or a future rate-limit service.
    /// </summary>
    [HttpPost("api/rooms/{roomId}/human")]
    public async Task<ActionResult<ChatEnvelope>> PostHumanMessage(
        string roomId,
        [FromBody] HumanMessageRequest request)
    {
        try
        {
            var envelope = await _runtime.PostHumanMessageAsync(roomId, request.Content);

            // System status + orchestration are best-effort — don't fail the request
            try
            {
                await _runtime.PostSystemStatusAsync(roomId, "Human message received — notifying agents.");
                _orchestrator.HandleHumanMessage(roomId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Best-effort post-message actions failed for room '{RoomId}'", roomId);
            }

            return Ok(envelope);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_message", message = ex.Message });
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
        if (request is null)
            return BadRequest(new { code = "invalid_phase_request", message = "Phase transition payload is required." });

        try
        {
            var snapshot = await _runtime.TransitionPhaseAsync(
                roomId, request.TargetPhase, request.Reason);
            return Ok(snapshot);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_phase_request", message = ex.Message });
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

    /// <summary>
    /// POST /api/rooms/{roomId}/compact — reset agent sessions for a room.
    /// Invalidates cached CLI sessions to free context window space.
    /// Note: The exact count of compacted sessions is not returned by the executor;
    /// we report the agent count as the upper bound, matching v1 behavior.
    /// </summary>
    [HttpPost("api/rooms/{roomId}/compact")]
    public async Task<IActionResult> CompactRoom(string roomId)
    {
        try
        {
            var totalAgents = _runtime.GetConfiguredAgents().Count;

            if (_executor.IsFullyOperational)
            {
                await _executor.InvalidateRoomSessionsAsync(roomId);
                return Ok(new { compactedSessions = totalAgents, totalAgents });
            }

            return Ok(new
            {
                compactedSessions = 0,
                totalAgents,
                note = "Executor is not fully operational; no sessions to compact."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compact sessions for room '{RoomId}'", roomId);
            return Problem("Failed to compact room sessions.");
        }
    }
}

/// <summary>
/// Request body for human message endpoint.
/// </summary>
public record HumanMessageRequest(string Content);
