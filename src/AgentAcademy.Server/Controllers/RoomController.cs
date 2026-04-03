using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Room CRUD, artifacts, usage, errors, and evaluation endpoints.
/// </summary>
[ApiController]
[Route("api/rooms")]
public class RoomController : ControllerBase
{
    private readonly WorkspaceRuntime _runtime;
    private readonly ILogger<RoomController> _logger;

    public RoomController(WorkspaceRuntime runtime, ILogger<RoomController> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/rooms — list all rooms.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<RoomSnapshot>>> GetRooms()
    {
        try
        {
            var rooms = await _runtime.GetRoomsAsync();
            return Ok(rooms);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list rooms");
            return Problem("Failed to retrieve rooms.");
        }
    }

    /// <summary>
    /// GET /api/rooms/{roomId} — room details.
    /// </summary>
    [HttpGet("{roomId}")]
    public async Task<ActionResult<RoomSnapshot>> GetRoom(string roomId)
    {
        try
        {
            var room = await _runtime.GetRoomAsync(roomId);
            if (room is null)
                return NotFound(new { code = "room_not_found", message = $"Room '{roomId}' not found" });

            return Ok(room);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get room '{RoomId}'", roomId);
            return Problem("Failed to retrieve room.");
        }
    }

    [HttpGet("{roomId}/messages")]
    public async Task<ActionResult<RoomMessagesResponse>> GetRoomMessages(
        string roomId,
        [FromQuery] string? after = null,
        [FromQuery] int limit = 50)
    {
        try
        {
            var (messages, hasMore) = await _runtime.GetRoomMessagesAsync(roomId, after, limit);
            return Ok(new RoomMessagesResponse(messages, hasMore));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { code = "room_not_found", message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get messages for room '{RoomId}'", roomId);
            return Problem("Failed to retrieve messages.");
        }
    }

    /// <summary>
    /// GET /api/rooms/{roomId}/artifacts — artifacts produced in a room.
    /// Artifact tracking will be wired when AgentEventTracker is ported.
    /// </summary>
    [HttpGet("{roomId}/artifacts")]
    public IActionResult GetRoomArtifacts(string roomId)
    {
        // AgentEventTracker is not yet ported — return empty list.
        return Ok(Array.Empty<ArtifactRecord>());
    }

    /// <summary>
    /// GET /api/rooms/{roomId}/usage — token usage stats for a room.
    /// Usage tracking will be wired when AgentEventTracker is ported.
    /// </summary>
    [HttpGet("{roomId}/usage")]
    public IActionResult GetRoomUsage(string roomId)
    {
        // AgentEventTracker is not yet ported — return zeroed summary.
        return Ok(new UsageSummary(
            TotalInputTokens: 0,
            TotalOutputTokens: 0,
            TotalCost: 0m,
            RequestCount: 0,
            Models: new List<string>()
        ));
    }

    /// <summary>
    /// GET /api/rooms/{roomId}/errors — agent errors in a room.
    /// Error tracking will be wired when AgentEventTracker is ported.
    /// </summary>
    [HttpGet("{roomId}/errors")]
    public IActionResult GetRoomErrors(string roomId)
    {
        // AgentEventTracker is not yet ported — return empty list.
        return Ok(Array.Empty<ErrorRecord>());
    }

    /// <summary>
    /// GET /api/rooms/{roomId}/evaluations — artifact evaluations for a room.
    /// Evaluation will be wired when the ArtifactEvaluator service is ported.
    /// </summary>
    [HttpGet("{roomId}/evaluations")]
    public IActionResult GetRoomEvaluations(string roomId)
    {
        // ArtifactEvaluator is not yet ported — return empty result.
        return Ok(new
        {
            artifacts = Array.Empty<EvaluationResult>(),
            aggregateScore = 0.0
        });
    }

    /// <summary>
    /// PUT /api/rooms/{roomId}/name — rename a room.
    /// </summary>
    [HttpPut("{roomId}/name")]
    public async Task<ActionResult<RoomSnapshot>> RenameRoom(string roomId, [FromBody] RenameRoomRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { code = "invalid_name", message = "Room name cannot be empty" });

        try
        {
            var room = await _runtime.RenameRoomAsync(roomId, request.Name.Trim());
            if (room is null)
                return NotFound(new { code = "room_not_found", message = $"Room '{roomId}' not found" });

            return Ok(room);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename room '{RoomId}'", roomId);
            return Problem("Failed to rename room.");
        }
    }
}

public record RenameRoomRequest(string Name);
