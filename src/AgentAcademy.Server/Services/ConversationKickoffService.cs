using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Posts a one-time system kickoff message in a room when no human/agent
/// conversation has started yet. Idempotent: once any Agent or User message
/// exists, subsequent calls are no-ops.
/// </summary>
public sealed class ConversationKickoffService
{
    private readonly AgentAcademyDbContext _db;
    private readonly IMessageService _messageService;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ILogger<ConversationKickoffService> _logger;

    public ConversationKickoffService(
        AgentAcademyDbContext db,
        IMessageService messageService,
        IAgentOrchestrator orchestrator,
        ILogger<ConversationKickoffService> logger)
    {
        _db = db;
        _messageService = messageService;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>
    /// Posts a kickoff system message and triggers orchestration if the room
    /// has no human or agent messages yet. Returns true if kickoff was performed.
    /// </summary>
    public async Task<bool> TryKickoffAsync(
        string roomId, string? activeWorkspace, CancellationToken ct = default)
    {
        var hasConversation = await _db.Messages.AnyAsync(m =>
            m.RoomId == roomId &&
            (m.SenderKind == nameof(MessageSenderKind.Agent) ||
             m.SenderKind == nameof(MessageSenderKind.User)), ct);

        if (hasConversation)
            return false;

        var content = activeWorkspace is not null
            ? $"Workspace ready: `{activeWorkspace}`. Team assembled. Aristotle, assess the current state and propose next steps."
            : "Team assembled. No workspace is active — onboard a project to begin.";

        await _messageService.PostSystemMessageAsync(roomId, content);
        _orchestrator.HandleHumanMessage(roomId);

        _logger.LogInformation(
            "Conversation kickoff: posted system message and triggered orchestration for room {RoomId}",
            roomId);

        return true;
    }
}
