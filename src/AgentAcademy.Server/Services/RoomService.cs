using System.Text.RegularExpressions;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles room operations: CRUD, queries, default room management,
/// phase transitions, room messages, and room snapshots.
/// Lifecycle operations (close, reopen, archive, cleanup) are on <see cref="RoomLifecycleService"/>.
/// </summary>
public sealed class RoomService
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
    private readonly ILogger<RoomService> _logger;
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityPublisher _activity;
    private readonly ConversationSessionService _sessionService;
    private readonly MessageService _messages;

    public RoomService(
        AgentAcademyDbContext db,
        ILogger<RoomService> logger,
        AgentCatalogOptions catalog,
        ActivityPublisher activity,
        ConversationSessionService sessionService,
        MessageService messages)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activity = activity;
        _sessionService = sessionService;
        _messages = messages;
    }

    // ── Room Queries ────────────────────────────────────────────

    /// <summary>
    /// Returns rooms for the active workspace as snapshots, ordered by name.
    /// Rooms without a workspace are included only when no workspace is active.
    /// </summary>
    public async Task<List<RoomSnapshot>> GetRoomsAsync(bool includeArchived = false)
    {
        var activeWorkspace = await GetActiveWorkspacePathAsync();

        IQueryable<RoomEntity> query = _db.Rooms;
        if (activeWorkspace is not null)
        {
            query = query.Where(r => r.WorkspacePath == activeWorkspace || r.WorkspacePath == null);
        }
        else
        {
            query = query.Where(r => r.WorkspacePath == null);
        }

        if (!includeArchived)
        {
            query = query.Where(r => r.Status != nameof(RoomStatus.Archived));
        }

        var rooms = await query
            .OrderBy(r => (r.Name.Contains("Main") && (r.Name.Contains("Room") || r.Name.Contains("Collaboration"))) ? 0 : 1)
            .ThenBy(r => r.Name)
            .ToListAsync();

        var allLocations = await _db.AgentLocations.ToListAsync();
        var locationsByRoom = allLocations
            .GroupBy(l => l.RoomId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var snapshots = new List<RoomSnapshot>();
        foreach (var room in rooms)
        {
            var locations = locationsByRoom.GetValueOrDefault(room.Id, []);
            snapshots.Add(await BuildRoomSnapshotAsync(room, locations));
        }
        return snapshots;
    }

    /// <summary>
    /// Returns a single room snapshot by ID, or null if not found.
    /// </summary>
    public async Task<RoomSnapshot?> GetRoomAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId);
        if (room is null) return null;
        return await BuildRoomSnapshotAsync(room);
    }

    /// <summary>
    /// Returns messages in a room with cursor-based pagination.
    /// Only returns non-DM messages from the active conversation session.
    /// </summary>
    public async Task<(List<ChatEnvelope> Messages, bool HasMore)> GetRoomMessagesAsync(
        string roomId, string? afterMessageId = null, int limit = 50, string? sessionId = null)
    {
        limit = Math.Clamp(limit, 1, 200);

        var room = await _db.Rooms.FindAsync(roomId);
        if (room is null)
            throw new InvalidOperationException($"Room '{roomId}' not found.");

        string? targetSessionId = sessionId;
        if (targetSessionId is null)
        {
            var activeSession = await _db.ConversationSessions
                .Where(s => s.RoomId == roomId && s.Status == "Active")
                .FirstOrDefaultAsync();
            targetSessionId = activeSession?.Id;
        }

        IQueryable<MessageEntity> query = _db.Messages
            .Where(m => m.RoomId == roomId && m.RecipientId == null
                && (targetSessionId == null || m.SessionId == targetSessionId
                    || m.SessionId == null || m.SenderKind == nameof(MessageSenderKind.User)));

        if (!string.IsNullOrEmpty(afterMessageId))
        {
            var cursor = await _db.Messages
                .Where(m => m.Id == afterMessageId && m.RoomId == roomId)
                .Select(m => new { m.SentAt, m.Id })
                .FirstOrDefaultAsync();

            if (cursor is not null)
            {
                query = query.Where(m =>
                    m.SentAt > cursor.SentAt ||
                    (m.SentAt == cursor.SentAt && string.Compare(m.Id, cursor.Id) > 0));
            }
        }

        var messages = await query
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id)
            .Take(limit + 1)
            .ToListAsync();

        var hasMore = messages.Count > limit;
        if (hasMore)
            messages = messages.Take(limit).ToList();

        return (messages.Select(BuildChatEnvelope).ToList(), hasMore);
    }

    /// <summary>
    /// Resolves the project name for a room by following
    /// roomId → RoomEntity.WorkspacePath → WorkspaceEntity.ProjectName.
    /// Falls back to the workspace directory basename when ProjectName is null.
    /// </summary>
    public async Task<string?> GetProjectNameForRoomAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId);
        if (room?.WorkspacePath is null) return null;

        var workspace = await _db.Workspaces.FindAsync(room.WorkspacePath);
        if (workspace is null) return null;

        if (!string.IsNullOrWhiteSpace(workspace.ProjectName))
            return workspace.ProjectName;

        var basename = Path.GetFileName(workspace.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return ProjectScanner.HumanizeProjectName(basename);
    }

    /// <summary>
    /// Returns the humanized project name of the active workspace.
    /// Falls back to directory basename when ProjectName is null.
    /// </summary>
    public async Task<string?> GetActiveProjectNameAsync()
    {
        var workspace = await _db.Workspaces
            .Where(w => w.IsActive)
            .FirstOrDefaultAsync();
        if (workspace is null) return null;

        if (!string.IsNullOrWhiteSpace(workspace.ProjectName))
            return workspace.ProjectName;

        var basename = Path.GetFileName(workspace.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return ProjectScanner.HumanizeProjectName(basename);
    }

    /// <summary>
    /// Returns IDs of rooms whose most recent message was sent by a human user
    /// and has not been followed by an agent or system response.
    /// </summary>
    public async Task<List<string>> GetRoomsWithPendingHumanMessagesAsync()
    {
        var activeWorkspace = await GetActiveWorkspacePathAsync();

        IQueryable<RoomEntity> query = _db.Rooms
            .Where(r => r.Status != nameof(RoomStatus.Archived)
                     && r.Status != nameof(RoomStatus.Completed))
            .Where(r => r.Messages.Any());

        if (activeWorkspace is not null)
            query = query.Where(r => r.WorkspacePath == activeWorkspace || r.WorkspacePath == null);
        else
            query = query.Where(r => r.WorkspacePath == null);

        var roomIds = await query
            .Where(r => r.Messages
                .OrderByDescending(m => m.SentAt)
                .ThenByDescending(m => m.Id)
                .Select(m => m.SenderKind)
                .FirstOrDefault() == nameof(MessageSenderKind.User))
            .Select(r => r.Id)
            .ToListAsync();

        return roomIds;
    }

    // ── Room CRUD ───────────────────────────────────────────────

    /// <summary>
    /// Creates a new room with the given name and optional description.
    /// </summary>
    public async Task<RoomSnapshot> CreateRoomAsync(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Room name is required", nameof(name));

        var now = DateTime.UtcNow;
        var slug = Normalize(name);
        if (string.IsNullOrEmpty(slug)) slug = "room";
        var roomId = $"{slug}-{Guid.NewGuid().ToString("N")[..8]}";
        var activeWorkspace = await GetActiveWorkspacePathAsync();

        var room = new RoomEntity
        {
            Id = roomId,
            Name = name,
            Status = nameof(RoomStatus.Idle),
            CurrentPhase = nameof(CollaborationPhase.Intake),
            WorkspacePath = activeWorkspace,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Rooms.Add(room);

        var welcomeContent = string.IsNullOrWhiteSpace(description)
            ? $"Room created: {name}"
            : $"Room created: {name}\n\n{description}";

        var welcomeMsg = new MessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RoomId = roomId,
            SenderId = "system",
            SenderName = "System",
            SenderKind = nameof(MessageSenderKind.System),
            Kind = nameof(MessageKind.System),
            Content = welcomeContent,
            SentAt = now
        };
        _db.Messages.Add(welcomeMsg);

        Publish(ActivityEventType.RoomCreated, roomId, null, null,
            $"Room created: {name}");

        await _db.SaveChangesAsync();

        _logger.LogInformation("Created room '{RoomId}' ({RoomName})", roomId, name);

        return await BuildRoomSnapshotAsync(room);
    }

    /// <summary>
    /// Renames a room and publishes a RoomRenamed activity event.
    /// Returns the updated snapshot, or null if the room doesn't exist.
    /// </summary>
    public async Task<RoomSnapshot?> RenameRoomAsync(string roomId, string newName)
    {
        var room = await _db.Rooms.FindAsync(roomId);
        if (room is null) return null;

        var oldName = room.Name;
        room.Name = newName;
        room.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        Publish(ActivityEventType.RoomRenamed, roomId, null, null,
            $"Room renamed: \"{oldName}\" → \"{newName}\"");

        _logger.LogInformation("Renamed room '{RoomId}' from '{OldName}' to '{NewName}'",
            roomId, oldName, newName);

        return await BuildRoomSnapshotAsync(room);
    }

    /// <summary>
    /// Sets or clears the topic of a room.
    /// </summary>
    public async Task<RoomSnapshot> SetRoomTopicAsync(string roomId, string? topic)
    {
        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found.");

        if (room.Status == nameof(RoomStatus.Archived))
            throw new InvalidOperationException($"Cannot set topic on archived room '{room.Name}'.");

        room.Topic = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();
        room.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.RoomStatusChanged, roomId, null, null,
            room.Topic is not null
                ? $"Room topic set: {room.Topic}"
                : "Room topic cleared");

        await _db.SaveChangesAsync();

        _logger.LogInformation("Set topic for room '{RoomId}' ({RoomName}): {Topic}",
            roomId, room.Name, room.Topic ?? "(cleared)");

        return await BuildRoomSnapshotAsync(room);
    }

    // ── Default Room Management ─────────────────────────────────

    /// <summary>
    /// Ensures a default room exists for the given workspace.
    /// Creates one if missing. Moves all agents to the workspace's default room.
    /// Returns the default room ID.
    /// </summary>
    public async Task<string> EnsureDefaultRoomForWorkspaceAsync(string workspacePath)
    {
        var existingForWorkspace = await _db.Rooms.FirstOrDefaultAsync(
            r => r.WorkspacePath == workspacePath &&
                 r.Id != _catalog.DefaultRoomId &&
                 (r.Name.EndsWith("Main Room") || r.Name.EndsWith("Collaboration Room")));

        if (existingForWorkspace is not null)
        {
            var defaultRoomId = existingForWorkspace.Id;

            if (existingForWorkspace.Name != _catalog.DefaultRoomName)
            {
                existingForWorkspace.Name = _catalog.DefaultRoomName;
                existingForWorkspace.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Updated default room name to '{RoomName}'", _catalog.DefaultRoomName);
            }

            await RetireLegacyDefaultRoomAsync(workspacePath, defaultRoomId);
            await MoveAllAgentsToRoomAsync(defaultRoomId);
            return defaultRoomId;
        }

        var slug = Normalize(Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar)));
        if (string.IsNullOrEmpty(slug)) slug = "project";
        var candidateId = $"{slug}-main";

        var collision = await _db.Rooms.FindAsync(candidateId);
        if (collision is not null && collision.WorkspacePath != workspacePath)
        {
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(workspacePath)))[..8].ToLowerInvariant();
            candidateId = $"{slug}-{hash}-main";
        }

        var now = DateTime.UtcNow;

        var room = new RoomEntity
        {
            Id = candidateId,
            Name = _catalog.DefaultRoomName,
            Status = nameof(RoomStatus.Idle),
            CurrentPhase = nameof(CollaborationPhase.Intake),
            WorkspacePath = workspacePath,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Rooms.Add(room);

        var workspace = await _db.Workspaces.FindAsync(workspacePath);
        var projectLabel = workspace?.ProjectName ?? slug;

        var welcomeMsg = new MessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RoomId = candidateId,
            SenderId = "system",
            SenderName = "System",
            SenderKind = nameof(MessageSenderKind.System),
            Kind = nameof(MessageKind.System),
            Content = $"Project loaded: {projectLabel}. Agents are ready.",
            SentAt = now
        };
        _db.Messages.Add(welcomeMsg);

        await _db.SaveChangesAsync();

        await RetireLegacyDefaultRoomAsync(workspacePath, candidateId);

        Publish(ActivityEventType.RoomCreated, candidateId, null, null,
            $"Default room created for workspace: {projectLabel}");

        _logger.LogInformation("Created default room '{RoomId}' for workspace '{Workspace}'",
            candidateId, workspacePath);

        await MoveAllAgentsToRoomAsync(candidateId);
        return candidateId;
    }

    /// <summary>
    /// If the legacy catalog default room was backfilled into this workspace,
    /// clear its WorkspacePath so it stops appearing alongside the real workspace default.
    /// </summary>
    internal async Task RetireLegacyDefaultRoomAsync(string workspacePath, string workspaceDefaultRoomId)
    {
        var legacyRoomId = _catalog.DefaultRoomId;
        if (legacyRoomId == workspaceDefaultRoomId) return;

        var legacyRoom = await _db.Rooms.FindAsync(legacyRoomId);
        if (legacyRoom is not null && legacyRoom.WorkspacePath == workspacePath)
        {
            legacyRoom.WorkspacePath = null;
            legacyRoom.Status = nameof(RoomStatus.Archived);
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "Retired legacy default room '{RoomId}' — archived and cleared WorkspacePath (was '{Workspace}')",
                legacyRoomId, workspacePath);
        }
    }

    /// <summary>
    /// Moves all configured agents to the specified room in Idle state.
    /// </summary>
    internal async Task MoveAllAgentsToRoomAsync(string roomId)
    {
        foreach (var agent in _catalog.Agents)
        {
            var loc = await _db.AgentLocations.FindAsync(agent.Id);
            if (loc is null)
            {
                _db.AgentLocations.Add(new AgentLocationEntity
                {
                    AgentId = agent.Id,
                    RoomId = roomId,
                    State = nameof(AgentState.Idle),
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                loc.RoomId = roomId;
                loc.BreakoutRoomId = null;
                loc.State = nameof(AgentState.Idle);
                loc.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
    }

    // ── Phase Transitions ───────────────────────────────────────

    /// <summary>
    /// Transitions a room (and its active task) to a new phase.
    /// </summary>
    public async Task<RoomSnapshot> TransitionPhaseAsync(
        string roomId, CollaborationPhase targetPhase, string? reason = null)
    {
        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found");

        if (room.CurrentPhase == targetPhase.ToString())
        {
            return await BuildRoomSnapshotAsync(room);
        }

        var now = DateTime.UtcNow;
        var oldPhase = room.CurrentPhase;

        var activeTask = await _db.Tasks
            .Where(t => t.RoomId == roomId && InProgressStatuses.Contains(t.Status))
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (activeTask is not null)
        {
            activeTask.CurrentPhase = targetPhase.ToString();
            activeTask.UpdatedAt = now;
        }

        var messageContent = $"Phase changed from {oldPhase} to {targetPhase}.";
        if (!string.IsNullOrWhiteSpace(reason))
            messageContent += $" {reason}";

        var msg = _messages.CreateMessageEntity(roomId, MessageKind.Coordination, messageContent, null, now);
        _db.Messages.Add(msg);

        room.CurrentPhase = targetPhase.ToString();
        room.Status = targetPhase == CollaborationPhase.FinalSynthesis
            ? nameof(RoomStatus.Completed)
            : nameof(RoomStatus.Active);
        room.UpdatedAt = now;

        Publish(ActivityEventType.PhaseChanged, roomId, null, activeTask?.Id,
            messageContent);

        await _db.SaveChangesAsync();

        return await BuildRoomSnapshotAsync(room);
    }

    // ── Startup Helpers ─────────────────────────────────────────

    /// <summary>
    /// Resolves the main room ID to use at startup for the given workspace.
    /// </summary>
    public async Task<string> ResolveStartupMainRoomIdAsync(string? activeWorkspace)
    {
        if (activeWorkspace is null)
        {
            return _catalog.DefaultRoomId;
        }

        var workspaceMainRoomId = await _db.Rooms
            .Where(r => r.WorkspacePath == activeWorkspace
                && (r.Name == _catalog.DefaultRoomName
                    || r.Name.EndsWith("Main Room")
                    || r.Name.EndsWith("Collaboration Room")))
            .OrderBy(r => r.Id == _catalog.DefaultRoomId ? 1 : 0)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        if (!string.IsNullOrWhiteSpace(workspaceMainRoomId))
        {
            return workspaceMainRoomId;
        }

        var legacyRoomExists = await _db.Rooms.AnyAsync(r => r.Id == _catalog.DefaultRoomId);
        if (legacyRoomExists)
        {
            return _catalog.DefaultRoomId;
        }

        return await EnsureDefaultRoomForWorkspaceAsync(activeWorkspace);
    }

    // ── Snapshot Builders ───────────────────────────────────────

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

        var messages = await _db.Messages
            .Where(m => m.RoomId == room.Id && m.RecipientId == null
                && (activeSessionId == null || m.SessionId == activeSessionId
                    || m.SessionId == null || m.SenderKind == nameof(MessageSenderKind.User)))
            .OrderByDescending(m => m.SentAt)
            .Take(MaxRecentMessages)
            .OrderBy(m => m.SentAt)
            .ToListAsync();

        var activeTaskEntity = await _db.Tasks
            .Where(t => t.RoomId == room.Id && InProgressStatuses.Contains(t.Status))
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        var activeTask = activeTaskEntity is null ? null : TaskQueryService.BuildTaskSnapshot(activeTaskEntity);

        var preferredRoles = activeTask?.PreferredRoles ?? [];
        var locations = preloadedLocations
            ?? await _db.AgentLocations.Where(l => l.RoomId == room.Id).ToListAsync();
        var participants = BuildParticipants(locations, preferredRoles);

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
            UpdatedAt: room.UpdatedAt
        );
    }

    internal List<AgentPresence> BuildParticipants(
        List<AgentLocationEntity> locations, List<string> preferredRoles)
    {
        var agentMap = _catalog.Agents.ToDictionary(a => a.Id);

        return locations
            .Where(l => agentMap.ContainsKey(l.AgentId) && l.BreakoutRoomId is null)
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

    // ── Private Helpers ─────────────────────────────────────────

    /// <summary>
    /// Returns the path of the currently active workspace, or null if none.
    /// </summary>
    public async Task<string?> GetActiveWorkspacePathAsync()
    {
        var active = await _db.Workspaces
            .Where(w => w.IsActive)
            .Select(w => w.Path)
            .FirstOrDefaultAsync();
        return active;
    }

    private ActivityEvent Publish(
        ActivityEventType type,
        string? roomId,
        string? actorId,
        string? taskId,
        string message,
        string? correlationId = null,
        ActivitySeverity severity = ActivitySeverity.Info)
        => _activity.Publish(type, roomId, actorId, taskId, message, correlationId, severity);

    internal static string Normalize(string value)
    {
        var lower = value.ToLowerInvariant();
        return Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
    }
}
