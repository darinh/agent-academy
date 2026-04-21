using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading.Channels;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Direct messaging endpoints for the DM UI.
/// Allows the human or consultant to view DM threads and send messages to agents.
/// </summary>
[ApiController]
public class DmController : ControllerBase
{
    private readonly IMessageService _messageService;
    private readonly IRoomService _roomService;
    private readonly IMessageBroadcaster _messageBroadcaster;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ILogger<DmController> _logger;

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DmController(
        IMessageService messageService,
        IRoomService roomService,
        IMessageBroadcaster messageBroadcaster,
        IAgentCatalog catalog,
        IAgentOrchestrator orchestrator,
        ILogger<DmController> logger)
    {
        _messageService = messageService;
        _roomService = roomService;
        _messageBroadcaster = messageBroadcaster;
        _catalog = catalog;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/dm/threads — list DM threads for the human, grouped by agent.
    /// </summary>
    [HttpGet("api/dm/threads")]
    public async Task<ActionResult<List<DmThreadSummary>>> GetThreads()
    {
        var threads = await _messageService.GetDmThreadsForHumanAsync();
        return Ok(threads);
    }

    /// <summary>
    /// GET /api/dm/threads/{agentId} — messages in a DM thread with a specific agent.
    /// </summary>
    [HttpGet("api/dm/threads/{agentId}")]
    public async Task<ActionResult<List<DmMessage>>> GetThreadMessages(string agentId)
    {
        var agents = _catalog.Agents;
        var agent = agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
            return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found.", "agent_not_found"));

        var messages = await _messageService.GetDmThreadMessagesAsync(agent.Id);

        var result = messages.Select(m => new DmMessage(
            Id: m.Id,
            SenderId: m.SenderId,
            SenderName: m.SenderName,
            SenderRole: m.SenderRole,
            Content: m.Content,
            SentAt: m.SentAt,
            IsFromHuman: m.SenderId == "human" || m.SenderId == "consultant"
        )).ToList();

        return Ok(result);
    }

    /// <summary>
    /// POST /api/dm/threads/{agentId} — human or consultant sends a DM to an agent.
    /// </summary>
    [HttpPost("api/dm/threads/{agentId}")]
    public async Task<ActionResult<DmMessage>> SendMessage(
        string agentId, [FromBody] SendDmRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(ApiProblem.BadRequest("Message content is required.", "invalid_message"));

        var agents = _catalog.Agents;
        var agent = agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
            return NotFound(ApiProblem.NotFound($"Agent '{agentId}' not found.", "agent_not_found"));

        // Derive identity from authenticated claims
        var isConsultant = User.IsInRole("Consultant");
        var senderId = isConsultant ? "consultant" : "human";
        var senderName = isConsultant ? "Consultant" : "Human";
        var senderRole = isConsultant ? "Consultant" : "Human";

        // Find the default room for context
        var rooms = await _roomService.GetRoomsAsync();
        var defaultRoom = rooms.FirstOrDefault();
        var roomId = defaultRoom?.Id ?? "main";

        var messageId = await _messageService.SendDirectMessageAsync(
            senderId: senderId,
            senderName: senderName,
            senderRole: senderRole,
            recipientId: agent.Id,
            message: request.Message,
            currentRoomId: roomId);

        // Trigger agent to respond promptly
        _orchestrator.HandleDirectMessage(agent.Id);

        _logger.LogInformation("{SenderRole} sent DM to {AgentName}: {Preview}",
            senderRole, agent.Name, request.Message.Length > 50 ? request.Message[..50] + "…" : request.Message);

        return StatusCode(201, new DmMessage(
            Id: messageId,
            SenderId: senderId,
            SenderName: senderName,
            SenderRole: senderRole,
            Content: request.Message,
            SentAt: DateTime.UtcNow,
            IsFromHuman: true
        ));
    }

    /// <summary>
    /// GET /api/dm/threads/{agentId}/stream — SSE stream of DM messages for a specific agent thread.
    /// Replays messages after the optional <paramref name="after"/> cursor,
    /// then streams live messages. Uses subscribe-first to avoid race conditions.
    /// Delivery is at-least-once: clients must deduplicate by message ID on reconnect overlap.
    /// If the client falls behind (channel overflow), emits a <c>resync</c> event and closes.
    /// </summary>
    [HttpGet("api/dm/threads/{agentId}/stream")]
    public async Task GetMessageStream(
        string agentId,
        [FromQuery] string? after = null,
        CancellationToken ct = default)
    {
        var agents = _catalog.Agents;
        var agent = agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            Response.StatusCode = 404;
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        var channel = Channel.CreateBounded<DmMessage>(
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
        var unsubscribe = _messageBroadcaster.SubscribeDm(agent.Id, msg =>
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
            var replay = await _messageService.GetDmThreadMessagesAsync(agent.Id, limit: 200, afterMessageId: after);

            foreach (var m in replay)
            {
                replayedIds.Add(m.Id);
                lastId = m.Id;
                var dm = new DmMessage(
                    Id: m.Id,
                    SenderId: m.SenderId,
                    SenderName: m.SenderName,
                    SenderRole: m.SenderRole,
                    Content: m.Content,
                    SentAt: m.SentAt,
                    IsFromHuman: m.SenderId == "human" || m.SenderId == "consultant"
                );
                await WriteSseEventAsync(Response, "message", dm, ct);
            }
            await Response.Body.FlushAsync(ct);

            // Stream live messages
            await foreach (var msg in channel.Reader.ReadAllAsync(ct))
            {
                if (replayedIds.Contains(msg.Id))
                    continue;
                replayedIds.Clear();

                lastId = msg.Id;
                await WriteSseEventAsync(Response, "message", msg, ct);
                await Response.Body.FlushAsync(ct);
            }

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
        finally
        {
            unsubscribe();
            channel.Writer.TryComplete();
        }
    }

    private static async Task WriteSseEventAsync(HttpResponse response, string eventName, DmMessage msg, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(msg, SseJsonOptions);
        await response.WriteAsync($"id: {msg.Id}\nevent: {eventName}\ndata: {json}\n\n", ct);
    }

    // ── Thread-list SSE (invalidation stream) ───────────────────

    /// <summary>
    /// GET /api/dm/threads/stream — SSE stream that notifies when any DM thread changes.
    /// Sends <c>thread-updated</c> events with <c>{"agentId":"…"}</c> whenever a DM is
    /// posted in any thread. Clients should debounce and refetch <c>GET /api/dm/threads</c>.
    /// </summary>
    [HttpGet("api/dm/threads/stream")]
    public async Task GetThreadListStream(CancellationToken ct = default)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        var channel = Channel.CreateBounded<(string AgentId, DmMessage Message)>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        var overflowed = false;

        var unsubscribe = _messageBroadcaster.SubscribeAllDm((agentId, msg) =>
        {
            if (!channel.Writer.TryWrite((agentId, msg)))
            {
                overflowed = true;
                channel.Writer.TryComplete();
            }
        });

        try
        {
            // Send a connected event so the client knows the stream is live.
            await Response.WriteAsync("event: connected\ndata: {}\n\n", ct);
            await Response.Body.FlushAsync(ct);

            await foreach (var (agentId, msg) in channel.Reader.ReadAllAsync(ct))
            {
                var data = JsonSerializer.Serialize(new { agentId, messageId = msg.Id }, SseJsonOptions);
                await Response.WriteAsync($"id: {msg.Id}\nevent: thread-updated\ndata: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }

            if (overflowed)
            {
                await Response.WriteAsync("event: resync\ndata: {}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal shutdown.
        }
        finally
        {
            unsubscribe();
            channel.Writer.TryComplete();
        }
    }
}

public record SendDmRequest([Required, MinLength(1), StringLength(50_000)] string Message);
