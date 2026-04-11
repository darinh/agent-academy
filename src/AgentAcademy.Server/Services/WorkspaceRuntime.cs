using System.Text.Json;
using System.Text.RegularExpressions;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Central state manager for the Agent Academy workspace.
/// Handles rooms, agents, messages, tasks, breakout rooms, plans,
/// and activity events. Ported from v1 TypeScript WorkspaceRuntime.
/// </summary>
public sealed class WorkspaceRuntime
{
    private const int MaxRecentMessages = 200;

    /// <summary>
    /// Task statuses that represent active/in-progress work (not terminal, not queued).
    /// </summary>
    private static readonly HashSet<string> InProgressStatuses = new(StringComparer.Ordinal)
    {
        nameof(Shared.Models.TaskStatus.Active),
        nameof(Shared.Models.TaskStatus.InReview),
        nameof(Shared.Models.TaskStatus.ChangesRequested),
        nameof(Shared.Models.TaskStatus.Approved),
        nameof(Shared.Models.TaskStatus.Merging),
        nameof(Shared.Models.TaskStatus.AwaitingValidation),
    };

    private static readonly HashSet<string> TerminalBreakoutStatuses =
        BreakoutRoomService.TerminalBreakoutStatuses;

    /// <summary>
    /// Task statuses that represent finished work (no further action expected).
    /// </summary>
    private static readonly HashSet<string> TerminalTaskStatuses = new(StringComparer.Ordinal)
    {
        nameof(Shared.Models.TaskStatus.Completed),
        nameof(Shared.Models.TaskStatus.Cancelled),
    };

    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<WorkspaceRuntime> _logger;
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityPublisher _activity;
    private readonly ConversationSessionService _sessionService;
    private readonly TaskQueryService _taskQueries;
    private readonly TaskLifecycleService _taskLifecycle;
    private readonly MessageService _messages;
    private readonly BreakoutRoomService _breakouts;
    private readonly TaskItemService _taskItems;

    public WorkspaceRuntime(
        AgentAcademyDbContext db,
        ILogger<WorkspaceRuntime> logger,
        AgentCatalogOptions catalog,
        ActivityPublisher activity,
        ConversationSessionService sessionService,
        TaskQueryService taskQueries,
        TaskLifecycleService taskLifecycle,
        MessageService messages,
        BreakoutRoomService breakouts,
        TaskItemService taskItems)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activity = activity;
        _sessionService = sessionService;
        _taskQueries = taskQueries;
        _taskLifecycle = taskLifecycle;
        _messages = messages;
        _breakouts = breakouts;
        _taskItems = taskItems;
    }

    // ── Initialization ──────────────────────────────────────────

    /// <summary>
    /// Returns the catalog's default room ID (e.g. "main").
    /// </summary>
    public string DefaultRoomId => _catalog.DefaultRoomId;

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

    /// <summary>
    /// Ensures the default room and agent locations exist.
    /// Call once at startup within a scope.
    /// Also tracks server instance lifecycle for crash detection.
    /// </summary>
    public async Task InitializeAsync()
    {
        // ── Server Instance Tracking ────────────────────────────
        await RecordServerInstanceAsync();

        var defaultRoomId = _catalog.DefaultRoomId;
        var activeWorkspace = await GetActiveWorkspacePathAsync();

        // When a workspace is active, EnsureDefaultRoomForWorkspaceAsync handles room creation.
        // Only create the legacy "main" room when no workspace exists (first boot).
        if (activeWorkspace is null)
        {
            var existing = await _db.Rooms.FindAsync(defaultRoomId);

            if (existing is null)
            {
                var now = DateTime.UtcNow;
                var room = new RoomEntity
                {
                    Id = defaultRoomId,
                    Name = _catalog.DefaultRoomName,
                    Status = nameof(RoomStatus.Idle),
                    CurrentPhase = nameof(CollaborationPhase.Intake),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _db.Rooms.Add(room);

                var welcomeMsg = new MessageEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    RoomId = defaultRoomId,
                    SenderId = "system",
                    SenderName = "System",
                    SenderKind = nameof(MessageSenderKind.System),
                    Kind = nameof(MessageKind.System),
                    Content = "Collaboration host started. Agents are loading.",
                    SentAt = now
                };
                _db.Messages.Add(welcomeMsg);

                await _db.SaveChangesAsync();

                Publish(ActivityEventType.RoomCreated, defaultRoomId, null, null,
                    $"Default room created: {_catalog.DefaultRoomName}");

                foreach (var agent in _catalog.Agents)
                {
                    Publish(ActivityEventType.AgentLoaded, defaultRoomId, agent.Id, null,
                        $"Agent loaded: {agent.Name} ({agent.Role})");
                }

                _logger.LogInformation("Created default room '{RoomName}' with {AgentCount} agents",
                    _catalog.DefaultRoomName, _catalog.Agents.Count);
            }
        }

        var startupMainRoomId = await ResolveStartupMainRoomIdAsync(activeWorkspace);

        // Initialize agent locations for any agent not already tracked
        foreach (var agent in _catalog.Agents)
        {
            var loc = await _db.AgentLocations.FindAsync(agent.Id);
            if (loc is null)
            {
                _db.AgentLocations.Add(new AgentLocationEntity
                {
                    AgentId = agent.Id,
                    RoomId = defaultRoomId,
                    State = nameof(AgentState.Idle),
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync();
    }

    public sealed record CrashRecoveryResult(
        int ClosedBreakoutRooms,
        int ResetWorkingAgents,
        int ResetTasks);

    /// <summary>
    /// Records a new server instance and detects if the previous one crashed
    /// (had no clean shutdown). The current instance ID is stored in
    /// <see cref="CurrentInstanceId"/> for the health endpoint.
    /// </summary>
    private async Task RecordServerInstanceAsync()
    {
        var version = typeof(WorkspaceRuntime).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";

        // Check for a previous instance that never shut down cleanly
        var orphan = await _db.ServerInstances
            .Where(si => si.ShutdownAt == null)
            .OrderByDescending(si => si.StartedAt)
            .FirstOrDefaultAsync();

        var crashDetected = false;
        if (orphan is not null)
        {
            // Previous instance didn't record a shutdown — it crashed.
            orphan.ShutdownAt = DateTime.UtcNow;
            orphan.ExitCode = -1;
            crashDetected = true;

            _logger.LogWarning(
                "Previous server instance {InstanceId} (started {StartedAt}) did not shut down cleanly — marking as crashed",
                orphan.Id, orphan.StartedAt);
        }

        var instance = new ServerInstanceEntity
        {
            StartedAt = DateTime.UtcNow,
            CrashDetected = crashDetected,
            Version = version
        };

        _db.ServerInstances.Add(instance);
        await _db.SaveChangesAsync();

        CurrentInstanceId = instance.Id;
        CurrentCrashDetected = crashDetected;

        _logger.LogInformation(
            "Server instance {InstanceId} started (version {Version}, crash detected: {Crash})",
            instance.Id, version, crashDetected);
    }

    /// <summary>
    /// The ID of the current server instance. Set during <see cref="InitializeAsync"/>.
    /// Used by the health endpoint for client reconnect protocol.
    /// </summary>
    public static string? CurrentInstanceId { get; private set; }

    /// <summary>
    /// Whether a crash was detected on the most recent startup
    /// (previous instance had no clean shutdown).
    /// </summary>
    public static bool CurrentCrashDetected { get; private set; }

    // ── Configured Agents ───────────────────────────────────────

    /// <summary>
    /// Returns the sorted list of configured agents from the catalog.
    /// </summary>
    public IReadOnlyList<AgentDefinition> GetConfiguredAgents() => _catalog.Agents;

    // ── Workspace Overview ──────────────────────────────────────

    /// <summary>
    /// Returns a full workspace overview: rooms, agents, activity, locations, breakouts.
    /// </summary>
    public async Task<WorkspaceOverview> GetOverviewAsync()
    {
        var rooms = await GetRoomsAsync();
        var agentLocations = await GetAgentLocationsAsync();
        var breakoutRooms = await GetAllBreakoutRoomsAsync();
        var recentActivity = _activity.GetRecentActivity();

        return new WorkspaceOverview(
            ConfiguredAgents: [.. _catalog.Agents],
            Rooms: rooms,
            RecentActivity: [.. recentActivity],
            AgentLocations: agentLocations,
            BreakoutRooms: breakoutRooms,
            GeneratedAt: DateTime.UtcNow
        );
    }

    // ── Room Management ─────────────────────────────────────────

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
            // Include workspace-scoped rooms AND legacy rooms with no workspace assignment
            query = query.Where(r => r.WorkspacePath == activeWorkspace || r.WorkspacePath == null);
        }
        else
        {
            // No active workspace — show rooms without a workspace assignment (legacy)
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

        // Pre-load all agent locations to avoid N+1 queries
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
    /// Returns true when the given room is the active workspace's main collaboration room
    /// or the legacy catalog default room.
    /// </summary>
    public async Task<bool> IsMainCollaborationRoomAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId);
        if (room is null)
            return false;

        if (room.Id == _catalog.DefaultRoomId)
            return true;

        var activeWorkspace = await GetActiveWorkspacePathAsync();
        if (activeWorkspace is null || room.WorkspacePath != activeWorkspace)
            return false;

        return room.Name == _catalog.DefaultRoomName
            || room.Name.EndsWith("Main Room", StringComparison.Ordinal)
            || room.Name.EndsWith("Collaboration Room", StringComparison.Ordinal);
    }

    /// <summary>
    /// Archives a non-main collaboration room. Already archived rooms are a no-op.
    /// </summary>
    public async Task CloseRoomAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found.");

        if (await IsMainCollaborationRoomAsync(roomId))
            throw new InvalidOperationException($"Room '{room.Name}' is the main collaboration room and cannot be closed.");

        if (room.Status == nameof(RoomStatus.Archived))
            return;

        var participantCount = await _db.AgentLocations
            .Where(l => l.RoomId == roomId && l.BreakoutRoomId == null)
            .CountAsync();

        if (participantCount > 0)
            throw new InvalidOperationException($"Room '{room.Name}' has {participantCount} active participant(s) and cannot be closed.");

        room.Status = nameof(RoomStatus.Archived);
        room.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.RoomClosed, roomId, null, null,
            $"Room archived: {room.Name}");

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Auto-archives a room when all its tasks have reached a terminal state
    /// (Completed or Cancelled). Moves agents back to the workspace default room.
    /// Skips main collaboration rooms.
    /// </summary>
    private async Task TryAutoArchiveRoomAsync(string roomId)
    {
        if (await IsMainCollaborationRoomAsync(roomId))
            return;

        var room = await _db.Rooms.FindAsync(roomId);
        if (room is null || room.Status == nameof(RoomStatus.Archived))
            return;

        // Check if ALL tasks in this room are terminal
        var hasNonTerminalTask = await _db.Tasks
            .Where(t => t.RoomId == roomId && !TerminalTaskStatuses.Contains(t.Status))
            .AnyAsync();

        if (hasNonTerminalTask)
            return;

        // Must have at least one task (don't archive rooms with zero tasks)
        var hasAnyTask = await _db.Tasks.AnyAsync(t => t.RoomId == roomId);
        if (!hasAnyTask)
            return;

        // Move agents out before archiving
        await EvacuateRoomAsync(roomId);

        room.Status = nameof(RoomStatus.Archived);
        room.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.RoomClosed, roomId, null, null,
            $"Room auto-archived (all tasks complete): {room.Name}");

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Auto-archived room '{RoomId}' ({RoomName}) — all tasks terminal",
            roomId, room.Name);
    }

    /// <summary>
    /// Moves all agents currently in a room back to the appropriate default room.
    /// Resolves the default room from the target room's WorkspacePath (not the global active workspace).
    /// </summary>
    private async Task EvacuateRoomAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId);
        var defaultRoomId = room?.WorkspacePath is not null
            ? await GetDefaultRoomForWorkspaceAsync(room.WorkspacePath)
            : _catalog.DefaultRoomId;

        var agentsInRoom = await _db.AgentLocations
            .Where(l => l.RoomId == roomId)
            .ToListAsync();

        foreach (var location in agentsInRoom)
        {
            location.RoomId = defaultRoomId;
            location.State = nameof(AgentState.Idle);
            location.BreakoutRoomId = null;
            location.UpdatedAt = DateTime.UtcNow;

            Publish(ActivityEventType.PresenceUpdated, defaultRoomId, location.AgentId, null,
                $"Agent {location.AgentId} returned to default room (room archived)");
        }
    }

    /// <summary>
    /// Returns the default room ID for a specific workspace path, falling back to the catalog default.
    /// </summary>
    private async Task<string> GetDefaultRoomForWorkspaceAsync(string workspacePath)
    {
        var workspaceDefaultRoom = await _db.Rooms
            .Where(r => r.WorkspacePath == workspacePath &&
                   (r.Name == _catalog.DefaultRoomName ||
                    r.Name.EndsWith("Main Room", StringComparison.Ordinal) ||
                    r.Name.EndsWith("Collaboration Room", StringComparison.Ordinal)))
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        return workspaceDefaultRoom ?? _catalog.DefaultRoomId;
    }

    /// <summary>
    /// Scans for stale rooms (all tasks terminal) that are still Active/Completed
    /// and archives them. Returns the count of rooms cleaned up.
    /// </summary>
    public async Task<int> CleanupStaleRoomsAsync()
    {
        // Find non-archived, non-main rooms that have at least one task
        var candidateRooms = await _db.Rooms
            .Where(r => r.Status != nameof(RoomStatus.Archived))
            .ToListAsync();

        var cleanedCount = 0;
        foreach (var room in candidateRooms)
        {
            if (await IsMainCollaborationRoomAsync(room.Id))
                continue;

            var tasks = await _db.Tasks
                .Where(t => t.RoomId == room.Id)
                .Select(t => t.Status)
                .ToListAsync();

            // Skip rooms with no tasks or any non-terminal tasks
            if (tasks.Count == 0 || tasks.Any(s => !TerminalTaskStatuses.Contains(s)))
                continue;

            await EvacuateRoomAsync(room.Id);

            room.Status = nameof(RoomStatus.Archived);
            room.UpdatedAt = DateTime.UtcNow;

            Publish(ActivityEventType.RoomClosed, room.Id, null, null,
                $"Room cleaned up (stale): {room.Name}");

            cleanedCount++;
        }

        if (cleanedCount > 0)
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation("Cleaned up {Count} stale room(s)", cleanedCount);
        }

        return cleanedCount;
    }
    /// Returns the created room snapshot.
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
    /// Reopens an archived room, restoring it to Idle status.
    /// </summary>
    public async Task<RoomSnapshot> ReopenRoomAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found.");

        if (room.Status != nameof(RoomStatus.Archived))
            throw new InvalidOperationException($"Room '{room.Name}' is not archived (current status: {room.Status}).");

        room.Status = nameof(RoomStatus.Idle);
        room.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.RoomStatusChanged, roomId, null, null,
            $"Room reopened: {room.Name}");

        await _db.SaveChangesAsync();

        _logger.LogInformation("Reopened room '{RoomId}' ({RoomName})", roomId, room.Name);

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

        // If a specific sessionId was requested, use that; otherwise use the active session
        string? targetSessionId = sessionId;
        if (targetSessionId is null)
        {
            var activeSession = await _db.ConversationSessions
                .Where(s => s.RoomId == roomId && s.Status == "Active")
                .FirstOrDefaultAsync();
            targetSessionId = activeSession?.Id;
        }

        // Human/User messages persist across session boundaries so external
        // inputs (consultant, human) remain visible after epoch transitions.
        IQueryable<Data.Entities.MessageEntity> query = _db.Messages
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
    /// Resolves the project name for a room by following
    /// roomId → RoomEntity.WorkspacePath → WorkspaceEntity.ProjectName.
    /// Returns null for legacy rooms without a workspace or workspaces without a project name.
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

        // Fallback: directory basename, humanized
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
    /// Creates the default room if no rooms exist.
    /// </summary>
    public async Task<RoomSnapshot> CreateDefaultRoomAsync()
    {
        var existing = await _db.Rooms.FindAsync(_catalog.DefaultRoomId);
        if (existing is not null)
        {
            return await BuildRoomSnapshotAsync(existing);
        }

        await InitializeAsync();
        var room = await _db.Rooms.FindAsync(_catalog.DefaultRoomId);
        return await BuildRoomSnapshotAsync(room!);
    }

    /// <summary>
    /// Ensures a default room exists for the given workspace.
    /// Creates one if missing. Moves all agents to the workspace's default room.
    /// Returns the default room ID.
    /// </summary>
    public async Task<string> EnsureDefaultRoomForWorkspaceAsync(string workspacePath)
    {
        // Check if this workspace already has a dedicated default room (not the legacy catalog room)
        var existingForWorkspace = await _db.Rooms.FirstOrDefaultAsync(
            r => r.WorkspacePath == workspacePath &&
                 r.Id != _catalog.DefaultRoomId &&
                 (r.Name.EndsWith("Main Room") || r.Name.EndsWith("Collaboration Room")));

        if (existingForWorkspace is not null)
        {
            var defaultRoomId = existingForWorkspace.Id;

            // Normalize room name to the catalog default
            if (existingForWorkspace.Name != _catalog.DefaultRoomName)
            {
                existingForWorkspace.Name = _catalog.DefaultRoomName;
                existingForWorkspace.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Updated default room name to '{RoomName}'", _catalog.DefaultRoomName);
            }

            // Retire the legacy catalog default room if it was backfilled into this workspace
            await RetireLegacyDefaultRoomAsync(workspacePath, defaultRoomId);

            // Move all agents to this workspace's default room
            await MoveAllAgentsToRoomAsync(defaultRoomId);
            return defaultRoomId;
        }

        // Generate a deterministic room ID from the workspace path
        var slug = Normalize(Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar)));
        if (string.IsNullOrEmpty(slug)) slug = "project";
        var candidateId = $"{slug}-main";

        // Check for ID collision with a different workspace's room
        var collision = await _db.Rooms.FindAsync(candidateId);
        if (collision is not null && collision.WorkspacePath != workspacePath)
        {
            // Use a stable hash (SHA-256 prefix) instead of GetHashCode (non-deterministic)
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

        // Retire the legacy catalog default room if it was backfilled into this workspace
        await RetireLegacyDefaultRoomAsync(workspacePath, candidateId);

        Publish(ActivityEventType.RoomCreated, candidateId, null, null,
            $"Default room created for workspace: {projectLabel}");

        _logger.LogInformation("Created default room '{RoomId}' for workspace '{Workspace}'",
            candidateId, workspacePath);

        // Move all agents to this workspace's default room
        await MoveAllAgentsToRoomAsync(candidateId);
        return candidateId;
    }

    /// <summary>
    /// If the legacy catalog default room (e.g. "main") was backfilled into this workspace
    /// by a migration, clear its WorkspacePath so it stops appearing alongside the real
    /// workspace default room.
    /// </summary>
    private async Task RetireLegacyDefaultRoomAsync(string workspacePath, string workspaceDefaultRoomId)
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
    private async Task MoveAllAgentsToRoomAsync(string roomId)
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

    // ── Task Management ─────────────────────────────────────────

    /// <summary>
    /// Creates a new task, optionally in an existing room or a new room.
    /// </summary>
    public async Task<TaskAssignmentResult> CreateTaskAsync(TaskAssignmentRequest request)
    {
        var now = DateTime.UtcNow;
        var correlationId = request.CorrelationId ?? Guid.NewGuid().ToString("N");

        // Room creation/lookup stays in WorkspaceRuntime (room/agent orchestration)
        RoomEntity roomEntity;
        bool isNewRoom;

        if (!string.IsNullOrEmpty(request.RoomId))
        {
            var existing = await _db.Rooms.FindAsync(request.RoomId);
            if (existing is null)
                throw new InvalidOperationException($"Room '{request.RoomId}' not found");

            existing.Status = nameof(RoomStatus.Active);
            existing.CurrentPhase = nameof(CollaborationPhase.Planning);
            existing.UpdatedAt = now;
            roomEntity = existing;
            isNewRoom = false;
        }
        else
        {
            var roomId = $"{Normalize(request.Title)}-{Guid.NewGuid().ToString("N")[..8]}";
            var activeWorkspace = await GetActiveWorkspacePathAsync();

            roomEntity = new RoomEntity
            {
                Id = roomId,
                Name = request.Title,
                Status = nameof(RoomStatus.Active),
                CurrentPhase = nameof(CollaborationPhase.Planning),
                WorkspacePath = activeWorkspace,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.Rooms.Add(roomEntity);
            isNewRoom = true;
        }

        // Delegate task entity creation, messages, and activity to TaskLifecycleService
        var (task, activity) = _taskLifecycle.StageNewTask(
            request, roomEntity.Id, roomEntity.WorkspacePath, isNewRoom, correlationId);

        await _taskLifecycle.AssociateTaskWithActiveSprintAsync(task.Id, roomEntity.WorkspacePath);

        await _db.SaveChangesAsync();

        // Auto-join agents into the new task room so GetIdleAgentsInRoomAsync finds them.
        if (isNewRoom)
        {
            foreach (var agent in _catalog.Agents.Where(a => a.AutoJoinDefaultRoom))
            {
                try
                {
                    var loc = await _db.AgentLocations.FindAsync(agent.Id);
                    if (loc is not null && loc.State == nameof(AgentState.Working))
                        continue;

                    await MoveAgentAsync(agent.Id, roomEntity.Id, AgentState.Idle);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to auto-join agent {AgentId} into room {RoomId}; skipping",
                        agent.Id, roomEntity.Id);
                }
            }
        }

        var roomSnapshot = await BuildRoomSnapshotAsync(roomEntity);

        return new TaskAssignmentResult(
            CorrelationId: correlationId,
            Room: roomSnapshot,
            Task: task,
            Activity: activity
        );
    }

    /// <summary>
    /// Returns all tasks.
    /// </summary>
    public async Task<List<TaskSnapshot>> GetTasksAsync(string? sprintId = null)
    {
        return await _taskQueries.GetTasksAsync(sprintId);
    }

    /// <summary>
    /// Returns a specific task by ID, or null if not found.
    /// </summary>
    public async Task<TaskSnapshot?> GetTaskAsync(string taskId)
    {
        return await _taskQueries.GetTaskAsync(taskId);
    }

    /// <summary>
    /// Finds a task by title. Returns the first non-cancelled match, or null.
    /// </summary>
    public async Task<TaskSnapshot?> FindTaskByTitleAsync(string title)
    {
        return await _taskQueries.FindTaskByTitleAsync(title);
    }

    /// <summary>
    /// Assigns an agent to a task. Validates the agent exists in the catalog.
    /// </summary>
    public async Task<TaskSnapshot> AssignTaskAsync(string taskId, string agentId, string agentName)
    {
        return await _taskQueries.AssignTaskAsync(taskId, agentId, agentName);
    }

    /// <summary>
    /// Updates a task's status. Automatically sets StartedAt/CompletedAt as appropriate.
    /// </summary>
    public async Task<TaskSnapshot> UpdateTaskStatusAsync(string taskId, Shared.Models.TaskStatus status)
    {
        return await _taskQueries.UpdateTaskStatusAsync(taskId, status);
    }

    /// <summary>
    /// Records a branch name on a task. Branch metadata is write-once per task.
    /// </summary>
    public async Task<TaskSnapshot> UpdateTaskBranchAsync(string taskId, string branchName)
    {
        return await _taskQueries.UpdateTaskBranchAsync(taskId, branchName);
    }

    /// <summary>
    /// Records PR information on a task.
    /// </summary>
    public async Task<TaskSnapshot> UpdateTaskPrAsync(
        string taskId, string url, int number, Shared.Models.PullRequestStatus status)
    {
        return await _taskQueries.UpdateTaskPrAsync(taskId, url, number, status);
    }

    /// <summary>
    /// Updates only the PR status on a task. Used by PR sync polling.
    /// Emits a TaskPrStatusChanged activity event when the status actually changes.
    /// Returns null if the status was already up-to-date.
    /// </summary>
    public async Task<TaskSnapshot?> SyncTaskPrStatusAsync(
        string taskId, Shared.Models.PullRequestStatus newStatus)
    {
        return await _taskLifecycle.SyncTaskPrStatusAsync(taskId, newStatus);
    }

    /// <summary>
    /// Returns task IDs that have open (non-terminal) pull requests for polling.
    /// </summary>
    public async Task<List<(string TaskId, int PrNumber)>> GetTasksWithActivePrsAsync()
    {
        return await _taskQueries.GetTasksWithActivePrsAsync();
    }
    public async Task<TaskSnapshot> CompleteTaskAsync(
        string taskId, int commitCount, List<string>? testsCreated = null, string? mergeCommitSha = null)
    {
        var (snapshot, roomId) = await _taskLifecycle.CompleteTaskCoreAsync(
            taskId, commitCount, testsCreated, mergeCommitSha);

        // Auto-archive the room if all tasks in it are terminal
        if (!string.IsNullOrEmpty(roomId))
        {
            await TryAutoArchiveRoomAsync(roomId);
        }

        return snapshot;
    }

    // ── Task State Commands ──────────────────────────────────────

    /// <summary>
    /// Claims a task for an agent. Prevents double-claiming by another agent.
    /// Auto-activates tasks in Queued status.
    /// </summary>
    public async Task<TaskSnapshot> ClaimTaskAsync(string taskId, string agentId, string agentName)
    {
        return await _taskLifecycle.ClaimTaskAsync(taskId, agentId, agentName);
    }

    /// <summary>
    /// Releases a task claim. Only the currently assigned agent can release.
    /// </summary>
    public async Task<TaskSnapshot> ReleaseTaskAsync(string taskId, string agentId)
    {
        return await _taskLifecycle.ReleaseTaskAsync(taskId, agentId);
    }

    /// <summary>
    /// Approves a task after review. Records the reviewer and increments review rounds.
    /// </summary>
    public async Task<TaskSnapshot> ApproveTaskAsync(string taskId, string reviewerAgentId, string? findings = null)
    {
        return await _taskLifecycle.ApproveTaskAsync(taskId, reviewerAgentId, findings);
    }

    /// <summary>
    /// Requests changes on a task after review. Records the reviewer and increments review rounds.
    /// </summary>
    public async Task<TaskSnapshot> RequestChangesAsync(string taskId, string reviewerAgentId, string findings)
    {
        return await _taskLifecycle.RequestChangesAsync(taskId, reviewerAgentId, findings);
    }

    /// <summary>
    /// Rejects an approved or completed task, returning it to ChangesRequested.
    /// For completed tasks, clears the merge metadata. Reopens the breakout room
    /// so the assigned agent can address the rejection findings.
    /// </summary>
    public async Task<TaskSnapshot> RejectTaskAsync(
        string taskId, string reviewerAgentId, string reason, string? revertCommitSha = null)
    {
        var result = await _taskLifecycle.RejectTaskCoreAsync(
            taskId, reviewerAgentId, reason, revertCommitSha);

        // Room/breakout reopen stays in WorkspaceRuntime (room/agent orchestration)
        if (!string.IsNullOrEmpty(result.RoomId))
        {
            await TryReopenRoomForTaskAsync(result.RoomId);
        }

        await TryReopenBreakoutForTaskAsync(result.TaskId, reason, result.ReviewerName);

        await _db.SaveChangesAsync();
        return result.Snapshot;
    }

    /// <summary>
    /// Reopens a room that was auto-archived when its task completed.
    /// No-op if the room is not archived.
    /// </summary>
    private async Task TryReopenRoomForTaskAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId);
        if (room is null || room.Status != nameof(RoomStatus.Archived))
            return;

        room.Status = nameof(RoomStatus.Active);
        room.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.RoomStatusChanged, roomId, null, null,
            $"Room reopened (task rejected): {room.Name}");

        _logger.LogInformation(
            "Reopened auto-archived room '{RoomId}' ({RoomName}) due to task rejection",
            roomId, room.Name);
    }

    /// <summary>
    /// Finds the most recent breakout room for a task and reopens it if archived.
    /// Moves the assigned agent back into the breakout to address rejection findings.
    /// </summary>
    private Task TryReopenBreakoutForTaskAsync(string taskId, string reason, string reviewerName)
        => _breakouts.TryReopenBreakoutForTaskAsync(taskId, reason, reviewerName);

    /// <summary>
    /// Returns tasks that are pending review (InReview or AwaitingValidation).
    /// </summary>
    public async Task<List<TaskSnapshot>> GetReviewQueueAsync()
    {
        return await _taskQueries.GetReviewQueueAsync();
    }

    /// <summary>
    /// Posts a system note to the room associated with a task.
    /// No-op if the task has no room.
    /// </summary>
    public async Task PostTaskNoteAsync(string taskId, string message)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        if (string.IsNullOrEmpty(entity.RoomId))
            return;

        await PostSystemStatusAsync(entity.RoomId, message);
    }

    // ── Task Comments ──────────────────────────────────────────

    /// <summary>
    /// Adds a comment or finding to a task.
    /// </summary>
    public async Task<TaskComment> AddTaskCommentAsync(
        string taskId, string agentId, string agentName,
        TaskCommentType commentType, string content)
    {
        return await _taskLifecycle.AddTaskCommentAsync(taskId, agentId, agentName, commentType, content);
    }

    /// <summary>
    /// Gets all comments for a task, ordered by creation time.
    /// </summary>
    public async Task<List<TaskComment>> GetTaskCommentsAsync(string taskId)
    {
        return await _taskQueries.GetTaskCommentsAsync(taskId);
    }

    /// <summary>
    /// Gets the count of comments for a task.
    /// </summary>
    public async Task<int> GetTaskCommentCountAsync(string taskId)
    {
        return await _taskQueries.GetTaskCommentCountAsync(taskId);
    }

    private static TaskComment BuildTaskComment(TaskCommentEntity entity)
        => TaskQueryService.BuildTaskComment(entity);

    // ── Task Evidence Ledger ──────────────────────────────────

    /// <summary>
    /// Valid evidence phases.
    /// </summary>
    public static readonly HashSet<string> ValidEvidencePhases = TaskLifecycleService.ValidEvidencePhases;

    /// <summary>
    /// Records a structured verification check against a task.
    /// </summary>
    public async Task<TaskEvidence> RecordEvidenceAsync(
        string taskId, string agentId, string agentName,
        EvidencePhase phase, string checkName, string tool,
        string? command, int? exitCode, string? outputSnippet, bool passed)
    {
        return await _taskLifecycle.RecordEvidenceAsync(
            taskId, agentId, agentName, phase, checkName, tool,
            command, exitCode, outputSnippet, passed);
    }

    /// <summary>
    /// Gets all evidence for a task, optionally filtered by phase.
    /// </summary>
    public async Task<List<TaskEvidence>> GetTaskEvidenceAsync(string taskId, EvidencePhase? phase = null)
    {
        return await _taskQueries.GetTaskEvidenceAsync(taskId, phase);
    }

    /// <summary>
    /// Checks whether a task meets the minimum evidence requirements for a phase transition.
    /// Gate definitions (based on task status):
    /// - Active → AwaitingValidation: ≥1 "After" check passed
    /// - AwaitingValidation → InReview: ≥2 "After" checks passed
    /// - InReview → Approved: ≥1 "Review" check passed
    /// </summary>
    public async Task<GateCheckResult> CheckGatesAsync(string taskId)
    {
        return await _taskLifecycle.CheckGatesAsync(taskId);
    }

    private static TaskEvidence BuildTaskEvidence(TaskEvidenceEntity entity)
        => TaskQueryService.BuildTaskEvidence(entity);

    // ── Spec–Task Linking ───────────────────────────────────────

    /// <summary>
    /// Valid spec-task link types.
    /// </summary>
    public static readonly HashSet<string> ValidLinkTypes = TaskLifecycleService.ValidLinkTypes;

    /// <summary>
    /// Links a task to a spec section. Idempotent — updates link type if the pair already exists.
    /// </summary>
    public async Task<SpecTaskLink> LinkTaskToSpecAsync(
        string taskId, string specSectionId, string agentId, string agentName,
        string linkType = "Implements", string? note = null)
    {
        return await _taskLifecycle.LinkTaskToSpecAsync(taskId, specSectionId, agentId, agentName, linkType, note);
    }

    /// <summary>
    /// Removes a spec-task link.
    /// </summary>
    public async Task UnlinkTaskFromSpecAsync(string taskId, string specSectionId)
    {
        await _taskQueries.UnlinkTaskFromSpecAsync(taskId, specSectionId);
    }

    /// <summary>
    /// Gets all spec links for a task.
    /// </summary>
    public async Task<List<SpecTaskLink>> GetSpecLinksForTaskAsync(string taskId)
    {
        return await _taskQueries.GetSpecLinksForTaskAsync(taskId);
    }

    /// <summary>
    /// Gets all tasks linked to a spec section.
    /// </summary>
    public async Task<List<SpecTaskLink>> GetTasksForSpecAsync(string specSectionId)
    {
        return await _taskQueries.GetTasksForSpecAsync(specSectionId);
    }

    /// <summary>
    /// Gets tasks that have no spec links.
    /// </summary>
    public async Task<List<TaskSnapshot>> GetUnlinkedTasksAsync()
    {
        return await _taskQueries.GetUnlinkedTasksAsync();
    }

    private static SpecTaskLink BuildSpecTaskLink(SpecTaskLinkEntity entity)
        => TaskQueryService.BuildSpecTaskLink(entity);

    // ── Message Management (delegated to MessageService) ────────

    /// <summary>
    /// Posts an agent message to a room.
    /// </summary>
    public Task<ChatEnvelope> PostMessageAsync(PostMessageRequest request)
        => _messages.PostMessageAsync(request);

    /// <summary>
    /// Posts a human message to a room.
    /// </summary>
    public Task<ChatEnvelope> PostHumanMessageAsync(
        string roomId, string content,
        string? userId = null, string? userName = null)
        => _messages.PostHumanMessageAsync(roomId, content, userId, userName);

    /// <summary>
    /// Posts a system message to a room (e.g. "Agent X joined the room.").
    /// </summary>
    public Task PostSystemMessageAsync(string roomId, string content)
        => _messages.PostSystemMessageAsync(roomId, content);

    /// <summary>
    /// Stores a direct message and posts a system notification in the recipient's room.
    /// </summary>
    public Task<string> SendDirectMessageAsync(
        string senderId, string senderName, string senderRole,
        string recipientId, string message, string currentRoomId)
        => _messages.SendDirectMessageAsync(senderId, senderName, senderRole, recipientId, message, currentRoomId);

    /// <summary>
    /// Returns recent DMs for an agent.
    /// </summary>
    public Task<List<MessageEntity>> GetDirectMessagesForAgentAsync(
        string agentId, int limit = 20, bool unreadOnly = true)
        => _messages.GetDirectMessagesForAgentAsync(agentId, limit, unreadOnly);

    /// <summary>
    /// Marks specific DMs as acknowledged by their IDs.
    /// </summary>
    public Task AcknowledgeDirectMessagesAsync(string agentId, IReadOnlyList<string> messageIds)
        => _messages.AcknowledgeDirectMessagesAsync(agentId, messageIds);

    /// <summary>
    /// Returns DM thread summaries for the human user, grouped by agent.
    /// </summary>
    public Task<List<DmThreadSummary>> GetDmThreadsForHumanAsync()
        => _messages.GetDmThreadsForHumanAsync();

    /// <summary>
    /// Returns messages in a DM thread between the human and a specific agent.
    /// </summary>
    public Task<List<MessageEntity>> GetDmThreadMessagesAsync(string agentId, int limit = 50)
        => _messages.GetDmThreadMessagesAsync(agentId, limit);

    // ── Phase Management ────────────────────────────────────────

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

        // Update active task phase if one exists
        var activeTask = await _db.Tasks
            .Where(t => t.RoomId == roomId && InProgressStatuses.Contains(t.Status))
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (activeTask is not null)
        {
            activeTask.CurrentPhase = targetPhase.ToString();
            activeTask.UpdatedAt = now;
        }

        // Add phase transition message
        var messageContent = $"Phase changed from {oldPhase} to {targetPhase}.";
        if (!string.IsNullOrWhiteSpace(reason))
            messageContent += $" {reason}";

        var msg = CreateMessageEntity(roomId, MessageKind.Coordination, messageContent, null, now);
        _db.Messages.Add(msg);

        // Update room
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

    // ── Agent Location ──────────────────────────────────────────

    /// <summary>
    /// Returns all agent locations.
    /// </summary>
    public async Task<List<AgentLocation>> GetAgentLocationsAsync()
    {
        var entities = await _db.AgentLocations.ToListAsync();
        return entities.Select(BuildAgentLocation).ToList();
    }

    /// <summary>
    /// Returns a single agent's location, or null if not tracked.
    /// </summary>
    public async Task<AgentLocation?> GetAgentLocationAsync(string agentId)
    {
        var entity = await _db.AgentLocations.FindAsync(agentId);
        return entity is null ? null : BuildAgentLocation(entity);
    }

    /// <summary>
    /// Moves an agent to a new room/state.
    /// </summary>
    public async Task<AgentLocation> MoveAgentAsync(
        string agentId, string roomId, AgentState state, string? breakoutRoomId = null)
    {
        // Allow both catalog agents and custom agents (stored in agent_configs)
        var inCatalog = _catalog.Agents.Any(a => a.Id == agentId);
        if (!inCatalog)
        {
            var customConfig = await _db.AgentConfigs.FindAsync(agentId);
            if (customConfig is null)
                throw new InvalidOperationException($"Agent '{agentId}' not found in catalog or custom agents");
        }

        var now = DateTime.UtcNow;
        var entity = await _db.AgentLocations.FindAsync(agentId);

        if (entity is null)
        {
            entity = new AgentLocationEntity
            {
                AgentId = agentId,
                RoomId = roomId,
                State = state.ToString(),
                BreakoutRoomId = breakoutRoomId,
                UpdatedAt = now
            };
            _db.AgentLocations.Add(entity);
        }
        else
        {
            entity.RoomId = roomId;
            entity.State = state.ToString();
            entity.BreakoutRoomId = breakoutRoomId;
            entity.UpdatedAt = now;
        }

        Publish(ActivityEventType.PresenceUpdated, roomId, agentId, null,
            $"Agent {agentId} moved to {roomId} ({state})");

        await _db.SaveChangesAsync();

        return BuildAgentLocation(entity);
    }

    // ── Breakout Rooms ──────────────────────────────────────────

    public Task<BreakoutRoom> CreateBreakoutRoomAsync(
        string parentRoomId, string agentId, string name)
        => _breakouts.CreateBreakoutRoomAsync(parentRoomId, agentId, name);

    public Task CloseBreakoutRoomAsync(
        string breakoutId,
        BreakoutRoomCloseReason closeReason = BreakoutRoomCloseReason.Completed)
        => _breakouts.CloseBreakoutRoomAsync(breakoutId, closeReason);

    public async Task<CrashRecoveryResult> RecoverFromCrashAsync(string mainRoomId)
    {
        var mainRoom = await _db.Rooms.FindAsync(mainRoomId)
            ?? throw new InvalidOperationException($"Room '{mainRoomId}' not found");

        var activeBreakoutIds = await _db.BreakoutRooms
            .Where(br => !TerminalBreakoutStatuses.Contains(br.Status))
            .OrderBy(br => br.CreatedAt)
            .Select(br => br.Id)
            .ToListAsync();

        foreach (var breakoutId in activeBreakoutIds)
        {
            await CloseBreakoutRoomAsync(breakoutId, BreakoutRoomCloseReason.ClosedByRecovery);
        }

        var activeBreakoutAssignments = await _db.BreakoutRooms
            .Where(br => !TerminalBreakoutStatuses.Contains(br.Status))
            .Select(br => br.Id)
            .ToListAsync();

        var lingeringWorkingAgents = await _db.AgentLocations
            .Where(loc => loc.State == nameof(AgentState.Working)
                && (loc.BreakoutRoomId == null || !activeBreakoutAssignments.Contains(loc.BreakoutRoomId)))
            .OrderBy(loc => loc.AgentId)
            .ToListAsync();

        foreach (var location in lingeringWorkingAgents)
        {
            await MoveAgentAsync(location.AgentId, location.RoomId, AgentState.Idle);
        }

        var activeAssigneeIds = await _db.AgentLocations
            .Where(loc => loc.State == nameof(AgentState.Working)
                && loc.BreakoutRoomId != null
                && activeBreakoutAssignments.Contains(loc.BreakoutRoomId))
            .Select(loc => loc.AgentId)
            .ToListAsync();

        var recoverableTasks = await _db.Tasks
            .Where(task => InProgressStatuses.Contains(task.Status)
                && !string.IsNullOrEmpty(task.AssignedAgentId)
                && !activeAssigneeIds.Contains(task.AssignedAgentId!))
            .OrderBy(task => task.CreatedAt)
            .ToListAsync();

        foreach (var task in recoverableTasks)
        {
            task.AssignedAgentId = null;
            task.AssignedAgentName = null;
            task.UpdatedAt = DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        var recoveredAnything = activeBreakoutIds.Count > 0
            || lingeringWorkingAgents.Count > 0
            || recoverableTasks.Count > 0;

        // Only post a recovery notification when there was actual work to report.
        // Prevents noisy "recovered from crash" messages when the server restarts
        // multiple times without any breakouts/agents needing recovery.
        if (recoveredAnything)
        {
            var message = $"System recovered from crash. Closed {activeBreakoutIds.Count} breakout room(s), reset {lingeringWorkingAgents.Count} stuck agent(s), and reset {recoverableTasks.Count} stuck task(s).";
            var recoveryCorrelationId = CurrentInstanceId;
            var alreadyNotified = !string.IsNullOrWhiteSpace(recoveryCorrelationId)
                && await _db.Messages.AnyAsync(m => m.RoomId == mainRoomId && m.CorrelationId == recoveryCorrelationId);

            if (!alreadyNotified)
            {
                var entity = CreateMessageEntity(mainRoomId, MessageKind.System, message, recoveryCorrelationId, now);
                _db.Messages.Add(entity);
                mainRoom.UpdatedAt = now;

                Publish(ActivityEventType.MessagePosted, mainRoomId, null, null,
                    $"System: {Truncate(message, 100)}", recoveryCorrelationId);
            }
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Crash recovery completed for room {RoomId}: closed {BreakoutCount} breakouts, reset {AgentCount} stuck agents, reset {TaskCount} stuck tasks (notification posted: {Posted})",
            mainRoomId, activeBreakoutIds.Count, lingeringWorkingAgents.Count, recoverableTasks.Count, recoveredAnything);

        return new CrashRecoveryResult(activeBreakoutIds.Count, lingeringWorkingAgents.Count, recoverableTasks.Count);
    }

    /// <summary>
    /// Returns IDs of rooms whose most recent message was sent by a human user
    /// and has not been followed by an agent or system response. These rooms
    /// need processing after a server restart.
    /// </summary>
    public async Task<List<string>> GetRoomsWithPendingHumanMessagesAsync()
    {
        var activeWorkspace = await GetActiveWorkspacePathAsync();

        IQueryable<RoomEntity> query = _db.Rooms
            .Where(r => r.Status != nameof(RoomStatus.Archived)
                     && r.Status != nameof(RoomStatus.Completed))
            .Where(r => r.Messages.Any());

        // Apply workspace scoping — same pattern as GetRoomsAsync
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

    public Task<BreakoutRoom?> GetBreakoutRoomAsync(string breakoutId)
        => _breakouts.GetBreakoutRoomAsync(breakoutId);

    public Task<List<BreakoutRoom>> GetBreakoutRoomsAsync(string parentRoomId)
        => _breakouts.GetBreakoutRoomsAsync(parentRoomId);

    public Task SetBreakoutTaskIdAsync(string breakoutRoomId, string taskId)
        => _breakouts.SetBreakoutTaskIdAsync(breakoutRoomId, taskId);

    public Task<string?> GetBreakoutTaskIdAsync(string breakoutRoomId)
        => _breakouts.GetBreakoutTaskIdAsync(breakoutRoomId);

    public Task<TaskSnapshot?> TransitionBreakoutTaskToInReviewAsync(string breakoutRoomId)
        => _breakouts.TransitionBreakoutTaskToInReviewAsync(breakoutRoomId);

    public Task<string> EnsureTaskForBreakoutAsync(
        string breakoutRoomId,
        string title,
        string description,
        string agentId,
        string roomId,
        string? currentPlan = null,
        string? branchName = null)
        => _breakouts.EnsureTaskForBreakoutAsync(breakoutRoomId, title, description, agentId, roomId, currentPlan, branchName);

    // ── Plan Management ─────────────────────────────────────────

    /// <summary>
    /// Returns the plan content for a room, or null if none exists.
    /// </summary>
    public async Task<PlanContent?> GetPlanAsync(string roomId)
    {
        var entity = await _db.Plans.FindAsync(roomId);
        return entity is null ? null : new PlanContent(entity.Content);
    }

    /// <summary>
    /// Creates or updates the plan for a room.
    /// </summary>
    public async Task SetPlanAsync(string roomId, string content)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new ArgumentException("Room ID is required.", nameof(roomId));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Plan content is required.", nameof(content));

        if (!await PlanTargetExistsAsync(roomId))
            throw new InvalidOperationException($"Room or breakout room '{roomId}' not found");

        var entity = await _db.Plans.FindAsync(roomId);
        var now = DateTime.UtcNow;

        if (entity is null)
        {
            _db.Plans.Add(new PlanEntity
            {
                RoomId = roomId,
                Content = content,
                UpdatedAt = now
            });
        }
        else
        {
            entity.Content = content;
            entity.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Deletes the plan for a room. Returns true if a plan was deleted.
    /// </summary>
    public async Task<bool> DeletePlanAsync(string roomId)
    {
        var entity = await _db.Plans.FindAsync(roomId);
        if (entity is null) return false;

        _db.Plans.Remove(entity);
        await _db.SaveChangesAsync();
        return true;
    }

    // ── Activity Publishing ─────────────────────────────────────

    /// <summary>
    /// Publishes an AgentThinking activity event.
    /// </summary>
    public async Task PublishThinkingAsync(AgentDefinition agent, string roomId)
    {
        Publish(ActivityEventType.AgentThinking, roomId, agent.Id, null,
            $"{agent.Name} is thinking...");
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Publishes an AgentFinished activity event.
    /// </summary>
    public async Task PublishFinishedAsync(AgentDefinition agent, string roomId)
    {
        Publish(ActivityEventType.AgentFinished, roomId, agent.Id, null,
            $"{agent.Name} finished.");
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns recent activity events from the singleton broadcaster.
    /// </summary>
    public IReadOnlyList<ActivityEvent> GetRecentActivity()
    {
        return _activity.GetRecentActivity();
    }

    /// <summary>
    /// Subscribes to activity events. Returns an unsubscribe action.
    /// </summary>
    public Action StreamActivity(Action<ActivityEvent> callback)
    {
        return _activity.Subscribe(callback);
    }

    // ── Private Helpers ─────────────────────────────────────────

    private ActivityEvent Publish(
        ActivityEventType type,
        string? roomId,
        string? actorId,
        string? taskId,
        string message,
        string? correlationId = null,
        ActivitySeverity severity = ActivitySeverity.Info)
        => _activity.Publish(type, roomId, actorId, taskId, message, correlationId, severity);

    private async Task<RoomSnapshot> BuildRoomSnapshotAsync(
        RoomEntity room, List<AgentLocationEntity>? preloadedLocations = null)
    {
        // Load messages from the active conversation session only.
        // Include messages with no SessionId for backwards compatibility
        // with pre-session data.
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

        // Get active task for this room
        var activeTaskEntity = await _db.Tasks
            .Where(t => t.RoomId == room.Id && InProgressStatuses.Contains(t.Status))
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        var activeTask = activeTaskEntity is null ? null : BuildTaskSnapshot(activeTaskEntity);

        // Build participants from actual agent locations in this room
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

    private List<AgentPresence> BuildParticipants(
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

    private static TaskSnapshot BuildTaskSnapshot(TaskEntity entity, int commentCount = 0)
        => TaskQueryService.BuildTaskSnapshot(entity, commentCount);

    private static ChatEnvelope BuildChatEnvelope(MessageEntity entity)
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

    private async Task<bool> PlanTargetExistsAsync(string roomId)
    {
        return await _db.Rooms.AnyAsync(r => r.Id == roomId)
            || await _db.BreakoutRooms.AnyAsync(br => br.Id == roomId);
    }

    private static AgentLocation BuildAgentLocation(AgentLocationEntity entity)
    {
        return new AgentLocation(
            AgentId: entity.AgentId,
            RoomId: entity.RoomId,
            State: Enum.Parse<AgentState>(entity.State),
            BreakoutRoomId: entity.BreakoutRoomId,
            UpdatedAt: entity.UpdatedAt
        );
    }

    private Task<List<BreakoutRoom>> GetAllBreakoutRoomsAsync()
        => _breakouts.GetAllBreakoutRoomsAsync();

    public Task<List<BreakoutRoom>> GetAgentSessionsAsync(string agentId)
        => _breakouts.GetAgentSessionsAsync(agentId);

    private MessageEntity CreateMessageEntity(
        string roomId, MessageKind kind, string content,
        string? correlationId, DateTime sentAt)
        => _messages.CreateMessageEntity(roomId, kind, content, correlationId, sentAt);

    private async Task<string> ResolveStartupMainRoomIdAsync(string? activeWorkspace)
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

    private Task TrimMessagesAsync(string roomId)
        => _messages.TrimMessagesAsync(roomId);

    private static string Normalize(string value)
    {
        var lower = value.ToLowerInvariant();
        return Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    // ── Orchestrator Support Methods ────────────────────────────

    /// <summary>
    /// Posts a system status message to a room (no agent sender required).
    /// </summary>
    public Task PostSystemStatusAsync(string roomId, string message)
        => _messages.PostSystemStatusAsync(roomId, message);

    /// <summary>
    /// Adds a message to a breakout room's message log.
    /// </summary>
    public Task PostBreakoutMessageAsync(
        string breakoutRoomId, string senderId, string senderName,
        string senderRole, string content)
        => _messages.PostBreakoutMessageAsync(breakoutRoomId, senderId, senderName, senderRole, content);

    /// <summary>
    /// Creates a task item associated with a breakout room.
    /// </summary>
    // ── Task Items (delegated to TaskItemService) ─────────────────

    public Task<TaskItem> CreateTaskItemAsync(
        string title, string description, string assignedTo,
        string roomId, string? breakoutRoomId)
        => _taskItems.CreateTaskItemAsync(title, description, assignedTo, roomId, breakoutRoomId);

    public Task UpdateTaskItemStatusAsync(
        string taskItemId, TaskItemStatus status, string? evidence = null)
        => _taskItems.UpdateTaskItemStatusAsync(taskItemId, status, evidence);

    public Task<List<TaskItem>> GetBreakoutTaskItemsAsync(string breakoutRoomId)
        => _taskItems.GetBreakoutTaskItemsAsync(breakoutRoomId);

    public Task<List<TaskItem>> GetActiveTaskItemsAsync()
        => _taskItems.GetActiveTaskItemsAsync();

    public Task<TaskItem?> GetTaskItemAsync(string taskItemId)
        => _taskItems.GetTaskItemAsync(taskItemId);

    public Task<List<TaskItem>> GetTaskItemsAsync(string? roomId = null, TaskItemStatus? status = null)
        => _taskItems.GetTaskItemsAsync(roomId, status);
}
