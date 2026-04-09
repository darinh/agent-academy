using AgentAcademy.Server.Data;
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
    private readonly AgentCatalogOptions _catalog;
    private readonly LlmUsageTracker _usageTracker;
    private readonly AgentErrorTracker _errorTracker;
    private readonly ILogger<RoomController> _logger;

    public RoomController(
        WorkspaceRuntime runtime,
        AgentCatalogOptions catalog,
        LlmUsageTracker usageTracker,
        AgentErrorTracker errorTracker,
        ILogger<RoomController> logger)
    {
        _runtime = runtime;
        _catalog = catalog;
        _usageTracker = usageTracker;
        _errorTracker = errorTracker;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/rooms — list all rooms. Archived rooms are excluded by default.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<RoomSnapshot>>> GetRooms([FromQuery] bool includeArchived = false)
    {
        try
        {
            var rooms = await _runtime.GetRoomsAsync(includeArchived);
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
        [FromQuery] int limit = 50,
        [FromQuery] string? sessionId = null)
    {
        try
        {
            var (messages, hasMore) = await _runtime.GetRoomMessagesAsync(roomId, after, limit, sessionId);
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
    /// GET /api/rooms/{roomId}/usage — aggregated token usage stats for a room.
    /// </summary>
    [HttpGet("{roomId}/usage")]
    public async Task<ActionResult<UsageSummary>> GetRoomUsage(string roomId)
    {
        try
        {
            var usage = await _usageTracker.GetRoomUsageAsync(roomId);
            return Ok(usage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get usage for room '{RoomId}'", roomId);
            return Problem("Failed to retrieve usage data.");
        }
    }

    /// <summary>
    /// GET /api/rooms/{roomId}/usage/agents — per-agent usage breakdown for a room.
    /// </summary>
    [HttpGet("{roomId}/usage/agents")]
    public async Task<ActionResult<List<AgentUsageSummary>>> GetRoomUsageByAgent(string roomId)
    {
        try
        {
            var breakdown = await _usageTracker.GetRoomUsageByAgentAsync(roomId);
            return Ok(breakdown);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get agent usage for room '{RoomId}'", roomId);
            return Problem("Failed to retrieve agent usage data.");
        }
    }

    /// <summary>
    /// GET /api/rooms/{roomId}/usage/records — individual LLM call records.
    /// </summary>
    [HttpGet("{roomId}/usage/records")]
    public async Task<ActionResult<List<LlmUsageRecord>>> GetRoomUsageRecords(
        string roomId,
        [FromQuery] string? agentId = null,
        [FromQuery] int limit = 50)
    {
        try
        {
            var records = await _usageTracker.GetRecentUsageAsync(roomId, agentId, Math.Clamp(limit, 1, 200));
            return Ok(records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get usage records for room '{RoomId}'", roomId);
            return Problem("Failed to retrieve usage records.");
        }
    }

    /// <summary>
    /// GET /api/rooms/{roomId}/errors — agent errors in a room.
    /// </summary>
    [HttpGet("{roomId}/errors")]
    public async Task<ActionResult<List<ErrorRecord>>> GetRoomErrors(string roomId, [FromQuery] int limit = 50)
    {
        try
        {
            var errors = await _errorTracker.GetRoomErrorsAsync(roomId, Math.Clamp(limit, 1, 200));
            return Ok(errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get errors for room '{RoomId}'", roomId);
            return Problem("Failed to retrieve error data.");
        }
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

    /// <summary>
    /// POST /api/rooms/cleanup — archive stale rooms where all tasks are complete.
    /// </summary>
    [HttpPost("cleanup")]
    public async Task<ActionResult> CleanupStaleRooms()
    {
        try
        {
            var count = await _runtime.CleanupStaleRoomsAsync();
            return Ok(new { archivedCount = count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup stale rooms");
            return Problem("Failed to cleanup stale rooms.");
        }
    }

    /// <summary>
    /// GET /api/rooms/{roomId}/sessions — conversation sessions for a room.
    /// </summary>
    [HttpGet("{roomId}/sessions")]
    public async Task<ActionResult<SessionListResponse>> GetRoomSessions(
        string roomId,
        [FromQuery] string? status = null,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        [FromServices] ConversationSessionService sessionService = default!)
    {
        try
        {
            var (sessions, totalCount) = await sessionService.GetRoomSessionsAsync(
                roomId, status, limit, offset);
            return Ok(new SessionListResponse(sessions, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sessions for room '{RoomId}'", roomId);
            return Problem("Failed to retrieve session history.");
        }
    }

    /// <summary>
    /// POST /api/rooms — create a new room.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<RoomSnapshot>> CreateRoom([FromBody] CreateRoomRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { code = "invalid_name", message = "Room name cannot be empty" });

        try
        {
            var room = await _runtime.CreateRoomAsync(request.Name.Trim(), request.Description?.Trim());
            return CreatedAtAction(nameof(GetRoom), new { roomId = room.Id }, room);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create room");
            return Problem("Failed to create room.");
        }
    }

    /// <summary>
    /// POST /api/rooms/{roomId}/sessions — create a new conversation session (archives current).
    /// </summary>
    [HttpPost("{roomId}/sessions")]
    public async Task<ActionResult<ConversationSessionSnapshot>> CreateSession(
        string roomId,
        [FromServices] ConversationSessionService sessionService)
    {
        try
        {
            var room = await _runtime.GetRoomAsync(roomId);
            if (room is null)
                return NotFound(new { code = "room_not_found", message = $"Room '{roomId}' not found" });

            var session = await sessionService.CreateNewSessionAsync(roomId);
            return CreatedAtAction(nameof(GetRoomSessions), new { roomId }, session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create session for room '{RoomId}'", roomId);
            return Problem("Failed to create session.");
        }
    }

    /// <summary>
    /// POST /api/rooms/{roomId}/agents/{agentId} — add an agent to this room.
    /// </summary>
    [HttpPost("{roomId}/agents/{agentId}")]
    public async Task<ActionResult<AgentLocation>> AddAgentToRoom(string roomId, string agentId,
        [FromServices] AgentAcademyDbContext db)
    {
        try
        {
            var room = await _runtime.GetRoomAsync(roomId);
            if (room is null)
                return NotFound(new { code = "room_not_found", message = $"Room '{roomId}' not found" });

            var agentName = _catalog.Agents.FirstOrDefault(a => a.Id == agentId)?.Name;
            if (agentName is null)
            {
                // Check custom agents
                var config = await db.AgentConfigs.FindAsync(agentId);
                if (config is null)
                    return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found" });
                agentName = agentId;
                if (!string.IsNullOrEmpty(config.CustomInstructions))
                {
                    try
                    {
                        var meta = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(config.CustomInstructions);
                        if (meta.TryGetProperty("displayName", out var dn)) agentName = dn.GetString() ?? agentId;
                    }
                    catch { /* use agentId */ }
                }
            }

            var location = await _runtime.MoveAgentAsync(agentId, roomId, AgentState.Idle);
            await _runtime.PostSystemMessageAsync(roomId, $"{agentName} joined the room.");
            return Ok(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add agent '{AgentId}' to room '{RoomId}'", agentId, roomId);
            return Problem("Failed to add agent to room.");
        }
    }

    /// <summary>
    /// DELETE /api/rooms/{roomId}/agents/{agentId} — remove an agent from this room (back to main).
    /// </summary>
    [HttpDelete("{roomId}/agents/{agentId}")]
    public async Task<ActionResult<AgentLocation>> RemoveAgentFromRoom(string roomId, string agentId,
        [FromServices] AgentAcademyDbContext db)
    {
        try
        {
            var room = await _runtime.GetRoomAsync(roomId);
            if (room is null)
                return NotFound(new { code = "room_not_found", message = $"Room '{roomId}' not found" });

            var agentName = _catalog.Agents.FirstOrDefault(a => a.Id == agentId)?.Name;
            if (agentName is null)
            {
                var config = await db.AgentConfigs.FindAsync(agentId);
                if (config is null)
                    return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found" });
                agentName = agentId;
                if (!string.IsNullOrEmpty(config.CustomInstructions))
                {
                    try
                    {
                        var meta = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(config.CustomInstructions);
                        if (meta.TryGetProperty("displayName", out var dn)) agentName = dn.GetString() ?? agentId;
                    }
                    catch { /* use agentId */ }
                }
            }

            var location = await _runtime.MoveAgentAsync(agentId, _catalog.DefaultRoomId, AgentState.Idle);
            await _runtime.PostSystemMessageAsync(roomId, $"{agentName} left the room.");
            return Ok(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove agent '{AgentId}' from room '{RoomId}'", agentId, roomId);
            return Problem("Failed to remove agent from room.");
        }
    }
}

public record RenameRoomRequest(string Name);
public record CreateRoomRequest(string Name, string? Description = null);
