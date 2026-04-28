using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading.Channels;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
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
    private readonly IRoomService _roomService;
    private readonly IAgentLocationService _agentLocationService;
    private readonly IMessageService _messageService;
    private readonly IMessageBroadcaster _messageBroadcaster;
    private readonly IAgentCatalog _catalog;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IAgentErrorTracker _errorTracker;
    private readonly IRoomArtifactTracker _artifactTracker;
    private readonly IArtifactEvaluatorService _evaluator;
    private readonly ILogger<RoomController> _logger;

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RoomController(
        IRoomService roomService,
        IAgentLocationService agentLocationService,
        IMessageService messageService,
        IMessageBroadcaster messageBroadcaster,
        IAgentCatalog catalog,
        ILlmUsageTracker usageTracker,
        IAgentErrorTracker errorTracker,
        IRoomArtifactTracker artifactTracker,
        IArtifactEvaluatorService evaluator,
        ILogger<RoomController> logger)
    {
        _roomService = roomService;
        _agentLocationService = agentLocationService;
        _messageService = messageService;
        _messageBroadcaster = messageBroadcaster;
        _catalog = catalog;
        _usageTracker = usageTracker;
        _errorTracker = errorTracker;
        _artifactTracker = artifactTracker;
        _evaluator = evaluator;
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
            var rooms = await _roomService.GetRoomsAsync(includeArchived);
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
            var room = await _roomService.GetRoomAsync(roomId);
            if (room is null)
                return NotFound(ApiProblem.NotFound($"Room '{roomId}' not found", "room_not_found"));

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
            var (messages, hasMore) = await _roomService.GetRoomMessagesAsync(roomId, after, limit, sessionId);
            return Ok(new RoomMessagesResponse(messages, hasMore));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiProblem.NotFound(ex.Message, "room_not_found"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get messages for room '{RoomId}'", roomId);
            return Problem("Failed to retrieve messages.");
        }
    }

    /// <summary>
    /// GET /api/rooms/{roomId}/artifacts — artifacts produced by agents in a room.
    /// Returns an append-only event log of file operations (write, commit, delete).
    /// </summary>
    [HttpGet("{roomId}/artifacts")]
    public async Task<ActionResult<List<ArtifactRecord>>> GetRoomArtifacts(
        string roomId, [FromQuery] int limit = 100)
    {
        try
        {
            var artifacts = await _artifactTracker.GetRoomArtifactsAsync(roomId, limit);
            return Ok(artifacts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artifacts for room '{RoomId}'", roomId);
            return Problem("Failed to retrieve artifact data.");
        }
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
    /// GET /api/rooms/{roomId}/context-usage — current context window usage per agent.
    /// Returns the latest input token count for each agent's most recent LLM call,
    /// along with the model's known context limit and usage percentage.
    /// </summary>
    [HttpGet("{roomId}/context-usage")]
    public async Task<ActionResult<List<AgentContextUsage>>> GetRoomContextUsage(string roomId)
    {
        try
        {
            var usage = await _usageTracker.GetLatestContextPerAgentAsync(roomId);
            return Ok(usage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get context usage for room '{RoomId}'", roomId);
            return Problem("Failed to retrieve context usage data.");
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
    /// Evaluates each tracked artifact file for existence, syntax, and completeness.
    /// </summary>
    [HttpGet("{roomId}/evaluations")]
    public async Task<IActionResult> GetRoomEvaluations(string roomId, CancellationToken ct)
    {
        try
        {
            var (artifacts, aggregateScore) = await _evaluator.EvaluateRoomArtifactsAsync(roomId, ct);
            return Ok(new { artifacts, aggregateScore });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate artifacts for room '{RoomId}'", roomId);
            return Problem("Failed to evaluate room artifacts.");
        }
    }

    /// <summary>
    /// PUT /api/rooms/{roomId}/name — rename a room.
    /// </summary>
    [HttpPut("{roomId}/name")]
    public async Task<ActionResult<RoomSnapshot>> RenameRoom(string roomId, [FromBody] RenameRoomRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiProblem.BadRequest("Room name cannot be empty", "invalid_name"));

        try
        {
            var room = await _roomService.RenameRoomAsync(roomId, request.Name.Trim());
            if (room is null)
                return NotFound(ApiProblem.NotFound($"Room '{roomId}' not found", "room_not_found"));

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
    /// Returns archived count plus per-room skip reasons so a result of
    /// <c>archivedCount=0</c> is debuggable without reading server logs.
    /// </summary>
    [HttpPost("cleanup")]
    public async Task<ActionResult> CleanupStaleRooms([FromServices] IRoomLifecycleService lifecycleService)
    {
        try
        {
            var result = await lifecycleService.CleanupStaleRoomsDetailedAsync();
            return Ok(new
            {
                archivedCount = result.ArchivedCount,
                skippedCount = result.SkippedCount,
                perRoomSkipReasons = result.Skips.Select(s => new
                {
                    roomId = s.RoomId,
                    roomName = s.RoomName,
                    reason = s.ReasonWireValue,
                }),
            });
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
        [FromServices] IConversationSessionQueryService sessionService = default!)
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
            return BadRequest(ApiProblem.BadRequest("Room name cannot be empty", "invalid_name"));

        try
        {
            var room = await _roomService.CreateRoomAsync(request.Name.Trim(), request.Description?.Trim());
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
        [FromServices] IConversationSessionService sessionService)
    {
        try
        {
            var room = await _roomService.GetRoomAsync(roomId);
            if (room is null)
                return NotFound(ApiProblem.NotFound($"Room '{roomId}' not found", "room_not_found"));

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
            var room = await _roomService.GetRoomAsync(roomId);
            if (room is null)
                return NotFound(ApiProblem.NotFound($"Room '{roomId}' not found", "room_not_found"));

            var agentName = _catalog.Agents.FirstOrDefault(a => a.Id == agentId)?.Name;
            if (agentName is null)
            {
                // Check custom agents
                var config = await db.AgentConfigs.FindAsync(agentId);
                if (config is null)
                    return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found", "agent_not_found"));
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

            var location = await _agentLocationService.MoveAgentAsync(agentId, roomId, AgentState.Idle);
            await _messageService.PostSystemMessageAsync(roomId, $"{agentName} joined the room.");
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
            var room = await _roomService.GetRoomAsync(roomId);
            if (room is null)
                return NotFound(ApiProblem.NotFound($"Room '{roomId}' not found", "room_not_found"));

            var agentName = _catalog.Agents.FirstOrDefault(a => a.Id == agentId)?.Name;
            if (agentName is null)
            {
                var config = await db.AgentConfigs.FindAsync(agentId);
                if (config is null)
                    return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found", "agent_not_found"));
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

            var location = await _agentLocationService.MoveAgentAsync(agentId, _catalog.DefaultRoomId, AgentState.Idle);
            await _messageService.PostSystemMessageAsync(roomId, $"{agentName} left the room.");
            return Ok(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove agent '{AgentId}' from room '{RoomId}'", agentId, roomId);
            return Problem("Failed to remove agent from room.");
        }
    }
    /// <summary>
    /// GET /api/rooms/{roomId}/messages/stream — SSE stream of room messages.
    /// Replays messages after the optional <paramref name="after"/> cursor,
    /// then streams live messages. Uses subscribe-first to avoid race conditions.
    /// Delivery is at-least-once: clients must deduplicate by message ID on reconnect overlap.
    /// If the client falls behind (channel overflow), emits a <c>resync</c> event and closes.
    /// </summary>
    [HttpGet("{roomId}/messages/stream")]
    public async Task GetMessageStream(
        string roomId,
        [FromQuery] string? after = null,
        CancellationToken ct = default)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        var channel = Channel.CreateBounded<ChatEnvelope>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        var replayedIds = new HashSet<string>();
        var lastId = after;
        var overflowed = false;

        // Subscribe BEFORE replaying to avoid a race where messages posted
        // between the DB query and subscription are silently dropped.
        var unsubscribe = _messageBroadcaster.Subscribe(roomId, msg =>
        {
            if (!channel.Writer.TryWrite(msg))
            {
                overflowed = true;
                channel.Writer.TryComplete();
            }
        });

        try
        {
            // Replay messages from DB after the cursor
            var (replayMessages, _) = await _roomService.GetRoomMessagesAsync(roomId, after, limit: 200);
            foreach (var msg in replayMessages)
            {
                replayedIds.Add(msg.Id);
                lastId = msg.Id;
                await WriteSseEventAsync(Response, "message", msg, ct);
            }
            await Response.Body.FlushAsync(ct);

            // Stream live messages
            await foreach (var msg in channel.Reader.ReadAllAsync(ct))
            {
                // Skip messages already sent during replay (dedup)
                if (replayedIds.Contains(msg.Id))
                    continue;
                replayedIds.Clear(); // No longer needed after first live message

                lastId = msg.Id;
                await WriteSseEventAsync(Response, "message", msg, ct);
                await Response.Body.FlushAsync(ct);
            }

            // If we exited the loop because of overflow, emit resync event
            if (overflowed)
            {
                var resyncData = JsonSerializer.Serialize(new { lastId }, SseJsonOptions);
                await Response.WriteAsync($"event: resync\ndata: {resyncData}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal shutdown.
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            Response.StatusCode = 404;
        }
        finally
        {
            unsubscribe();
            channel.Writer.TryComplete();
        }
    }

    private static async Task WriteSseEventAsync(HttpResponse response, string eventName, ChatEnvelope msg, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(msg, SseJsonOptions);
        await response.WriteAsync($"id: {msg.Id}\nevent: {eventName}\ndata: {json}\n\n", ct);
    }
}

public record RenameRoomRequest([Required, StringLength(200)] string Name);
public record CreateRoomRequest(
    [Required, StringLength(200)] string Name,
    [StringLength(1000)] string? Description = null);
