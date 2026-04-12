using System.ComponentModel.DataAnnotations;
using AgentAcademy.Server.Services;
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
    private readonly MessageService _messageService;
    private readonly RoomService _roomService;
    private readonly AgentCatalogOptions _catalog;
    private readonly AgentOrchestrator _orchestrator;
    private readonly ILogger<DmController> _logger;

    public DmController(
        MessageService messageService,
        RoomService roomService,
        AgentCatalogOptions catalog,
        AgentOrchestrator orchestrator,
        ILogger<DmController> logger)
    {
        _messageService = messageService;
        _roomService = roomService;
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
            return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found." });

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
            return BadRequest(new { code = "invalid_message", message = "Message content is required." });

        var agents = _catalog.Agents;
        var agent = agents.FirstOrDefault(
            a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
            return NotFound(new { code = "agent_not_found", message = $"Agent '{agentId}' not found." });

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
}

public record SendDmRequest([property: Required, MinLength(1), StringLength(50_000)] string Message);
