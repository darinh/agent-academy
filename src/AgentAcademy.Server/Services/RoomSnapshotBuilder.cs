using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Builds read-model snapshots of rooms including messages, active task, and participants.
/// Extracted from <see cref="RoomService"/> to isolate snapshot assembly from room mutations.
/// </summary>
public sealed class RoomSnapshotBuilder : IRoomSnapshotBuilder
{
    private const int MaxRecentMessages = 200;

    private static readonly HashSet<string> InProgressStatuses = new(StringComparer.Ordinal)
    {
        nameof(Shared.Models.TaskStatus.Active),
        nameof(Shared.Models.TaskStatus.InReview),
        nameof(Shared.Models.TaskStatus.ChangesRequested),
        nameof(Shared.Models.TaskStatus.Approved),
        nameof(Shared.Models.TaskStatus.Merging),
        nameof(Shared.Models.TaskStatus.AwaitingValidation),
    };

    private readonly AgentAcademyDbContext _db;
    private readonly IAgentCatalog _catalog;
    private readonly IPhaseTransitionValidator _phaseValidator;

    public RoomSnapshotBuilder(AgentAcademyDbContext db, IAgentCatalog catalog, IPhaseTransitionValidator phaseValidator)
    {
        _db = db;
        _catalog = catalog;
        _phaseValidator = phaseValidator;
    }

    /// <summary>
    /// Builds a full room snapshot including messages, active task, and participants.
    /// </summary>
    public async Task<RoomSnapshot> BuildRoomSnapshotAsync(
        RoomEntity room, List<AgentLocationEntity>? preloadedLocations = null)
    {
        var activeSession = await _db.ConversationSessions
            .Where(s => s.RoomId == room.Id && s.Status == "Active")
            .FirstOrDefaultAsync();

        var activeSessionId = activeSession?.Id;

        // Per spec 005 §Message Management: only the active session's messages plus
        // legacy untagged messages. When no active session exists, return ONLY legacy
        // untagged messages — do not leak prior-session history (#64).
        var messages = await _db.Messages
            .Where(m => m.RoomId == room.Id && m.RecipientId == null
                && (m.SessionId == null || m.SessionId == activeSessionId))
            .OrderByDescending(m => m.SentAt)
            .Take(MaxRecentMessages)
            .OrderBy(m => m.SentAt)
            .ToListAsync();

        var activeTaskEntity = await _db.Tasks
            .Where(t => t.RoomId == room.Id && InProgressStatuses.Contains(t.Status))
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        var activeTask = activeTaskEntity is null ? null : TaskSnapshotFactory.BuildTaskSnapshot(activeTaskEntity);

        var preferredRoles = activeTask?.PreferredRoles ?? [];
        var locations = preloadedLocations
            ?? await _db.AgentLocations.Where(l => l.RoomId == room.Id).ToListAsync();
        var participants = BuildParticipants(locations, preferredRoles, room.CurrentPhase);

        var phaseGates = await _phaseValidator.GetGatesAsync(room.Id);

        return new RoomSnapshot(
            Id: room.Id,
            Name: room.Name,
            Topic: room.Topic,
            Status: Enum.Parse<RoomStatus>(room.Status),
            CurrentPhase: Enum.Parse<CollaborationPhase>(room.CurrentPhase),
            ActiveTask: activeTask,
            Participants: participants,
            RecentMessages: messages.Select(BuildChatEnvelope).ToList(),
            CreatedAt: room.CreatedAt,
            UpdatedAt: room.UpdatedAt,
            PhaseGates: phaseGates
        );
    }

    internal List<AgentPresence> BuildParticipants(
        List<AgentLocationEntity> locations, List<string> preferredRoles, string? currentPhase = null)
    {
        var agentMap = _catalog.Agents.ToDictionary(a => a.Id);

        return locations
            .Where(l => agentMap.ContainsKey(l.AgentId) && l.BreakoutRoomId is null)
            .Where(l => currentPhase is null
                || SprintPreambles.IsRoleAllowedInStage(agentMap[l.AgentId].Role, currentPhase))
            .Select(l =>
            {
                var a = agentMap[l.AgentId];
                return new AgentPresence(
                    AgentId: a.Id,
                    Name: a.Name,
                    Role: a.Role,
                    Availability: preferredRoles.Contains(a.Role)
                        ? AgentAvailability.Preferred
                        : AgentAvailability.Ready,
                    IsPreferred: preferredRoles.Contains(a.Role),
                    LastActivityAt: l.UpdatedAt,
                    ActiveCapabilities: [.. a.CapabilityTags]
                );
            })
            .ToList();
    }

    internal static ChatEnvelope BuildChatEnvelope(MessageEntity entity)
    {
        return new ChatEnvelope(
            Id: entity.Id,
            RoomId: entity.RoomId,
            SenderId: entity.SenderId,
            SenderName: entity.SenderName,
            SenderRole: entity.SenderRole,
            SenderKind: Enum.Parse<MessageSenderKind>(entity.SenderKind),
            Kind: Enum.Parse<MessageKind>(entity.Kind),
            Content: entity.Content,
            SentAt: entity.SentAt,
            CorrelationId: entity.CorrelationId,
            ReplyToMessageId: entity.ReplyToMessageId
        );
    }
}
