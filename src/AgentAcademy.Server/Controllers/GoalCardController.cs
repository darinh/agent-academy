using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Goal card API — queryable by the operator/consultant to detect drift
/// across the agent team. Goal cards are structured intent artifacts that
/// agents create before starting significant work.
/// </summary>
[ApiController]
[Route("api/goal-cards")]
public class GoalCardController : ControllerBase
{
    private readonly IGoalCardService _goalCards;
    private readonly ILogger<GoalCardController> _logger;

    public GoalCardController(
        IGoalCardService goalCards,
        ILogger<GoalCardController> logger)
    {
        _goalCards = goalCards;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/goal-cards — list goal cards with optional filters.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<GoalCard>>> List(
        [FromQuery] string? roomId = null,
        [FromQuery] string? agentId = null,
        [FromQuery] string? taskId = null,
        [FromQuery] string? status = null,
        [FromQuery] string? verdict = null)
    {
        try
        {
            GoalCardStatus? statusFilter = null;
            if (!string.IsNullOrWhiteSpace(status) &&
                Enum.TryParse<GoalCardStatus>(status, ignoreCase: true, out var sf))
                statusFilter = sf;

            GoalCardVerdict? verdictFilter = null;
            if (!string.IsNullOrWhiteSpace(verdict) &&
                Enum.TryParse<GoalCardVerdict>(verdict, ignoreCase: true, out var vf))
                verdictFilter = vf;

            List<GoalCard> cards;

            if (!string.IsNullOrWhiteSpace(taskId))
            {
                cards = await _goalCards.GetByTaskAsync(taskId);
            }
            else if (!string.IsNullOrWhiteSpace(agentId))
            {
                cards = await _goalCards.GetByAgentAsync(agentId);
            }
            else
            {
                // Use QueryAsync to support all statuses, not just active
                cards = await _goalCards.QueryAsync(roomId, statusFilter, verdictFilter);
                return Ok(cards); // Already filtered at DB level
            }

            // Apply roomId, status, verdict filters to agent/task results
            if (!string.IsNullOrWhiteSpace(roomId))
                cards = cards.Where(c => c.RoomId == roomId).ToList();
            if (statusFilter.HasValue)
                cards = cards.Where(c => c.Status == statusFilter.Value).ToList();
            if (verdictFilter.HasValue)
                cards = cards.Where(c => c.Verdict == verdictFilter.Value).ToList();

            return Ok(cards);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list goal cards");
            return Problem("Failed to retrieve goal cards.");
        }
    }

    /// <summary>
    /// GET /api/goal-cards/{id} — get a specific goal card.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<GoalCard>> Get(string id)
    {
        try
        {
            var card = await _goalCards.GetByIdAsync(id);
            if (card is null)
                return NotFound(ApiProblem.NotFound($"Goal card '{id}' not found"));

            return Ok(card);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get goal card '{GoalCardId}'", id);
            return Problem("Failed to retrieve goal card.");
        }
    }

    /// <summary>
    /// PATCH /api/goal-cards/{id}/status — update goal card status.
    /// </summary>
    [HttpPatch("{id}/status")]
    public async Task<ActionResult<GoalCard>> UpdateStatus(
        string id,
        [FromBody] UpdateGoalCardStatusRequest request)
    {
        try
        {
            var card = await _goalCards.UpdateStatusAsync(id, request.Status);
            if (card is null)
                return NotFound(ApiProblem.NotFound($"Goal card '{id}' not found"));

            return Ok(card);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiProblem.BadRequest(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update goal card '{GoalCardId}' status", id);
            return Problem("Failed to update goal card status.");
        }
    }

    /// <summary>
    /// PATCH /api/goal-cards/{id}/task — link a goal card to a task.
    /// </summary>
    [HttpPatch("{id}/task")]
    public async Task<ActionResult<GoalCard>> AttachToTask(
        string id,
        [FromBody] AttachGoalCardToTaskRequest request)
    {
        try
        {
            var card = await _goalCards.AttachToTaskAsync(id, request.TaskId);
            if (card is null)
                return NotFound(ApiProblem.NotFound($"Goal card '{id}' not found"));

            return Ok(card);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiProblem.BadRequest(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return NotFound(ApiProblem.NotFound(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to attach goal card '{GoalCardId}' to task", id);
            return Problem("Failed to attach goal card to task.");
        }
    }
}

/// <summary>
/// Request to link a goal card to a task.
/// </summary>
public record AttachGoalCardToTaskRequest(string TaskId);
