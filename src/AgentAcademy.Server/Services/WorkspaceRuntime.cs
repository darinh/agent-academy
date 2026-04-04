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

    private static readonly HashSet<string> TerminalBreakoutStatuses = new(StringComparer.Ordinal)
    {
        nameof(RoomStatus.Completed),
        nameof(RoomStatus.Archived)
    };

    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<WorkspaceRuntime> _logger;
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityBroadcaster _activityBus;
    private readonly ConversationSessionService _sessionService;

    public WorkspaceRuntime(
        AgentAcademyDbContext db,
        ILogger<WorkspaceRuntime> logger,
        AgentCatalogOptions catalog,
        ActivityBroadcaster activityBus,
        ConversationSessionService sessionService)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activityBus = activityBus;
        _sessionService = sessionService;
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
        var recentActivity = _activityBus.GetRecentActivity();

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
    public async Task<List<RoomSnapshot>> GetRoomsAsync()
    {
        var activeWorkspace = await GetActiveWorkspacePathAsync();

        IQueryable<RoomEntity> query = _db.Rooms;
        if (activeWorkspace is not null)
        {
            query = query.Where(r => r.WorkspacePath == activeWorkspace);
        }
        else
        {
            // No active workspace — show rooms without a workspace assignment (legacy)
            query = query.Where(r => r.WorkspacePath == null);
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
    /// Returns messages in a room with cursor-based pagination.
    /// Only returns non-DM messages from the active conversation session.
    /// </summary>
    public async Task<(List<ChatEnvelope> Messages, bool HasMore)> GetRoomMessagesAsync(
        string roomId, string? afterMessageId = null, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        var room = await _db.Rooms.FindAsync(roomId);
        if (room is null)
            throw new InvalidOperationException($"Room '{roomId}' not found.");

        var activeSession = await _db.ConversationSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .FirstOrDefaultAsync();
        var activeSessionId = activeSession?.Id;

        // Human/User messages persist across session boundaries so external
        // inputs (consultant, human) remain visible after epoch transitions.
        IQueryable<Data.Entities.MessageEntity> query = _db.Messages
            .Where(m => m.RoomId == roomId && m.RecipientId == null
                && (activeSessionId == null || m.SessionId == activeSessionId
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
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "Retired legacy default room '{RoomId}' — cleared WorkspacePath (was '{Workspace}')",
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
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("Title is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Description))
            throw new ArgumentException("Description is required", nameof(request));

        var now = DateTime.UtcNow;
        var correlationId = request.CorrelationId ?? Guid.NewGuid().ToString("N");
        var taskId = Guid.NewGuid().ToString("N");

        var preferredRoles = request.PreferredRoles
            .Select(r => r.Trim())
            .Where(r => !string.IsNullOrEmpty(r))
            .Distinct()
            .ToList();
        var currentPlan = ResolveTaskPlanContent(request.Title, request.CurrentPlan);

        var task = new TaskSnapshot(
            Id: taskId,
            Title: request.Title,
            Description: request.Description,
            SuccessCriteria: request.SuccessCriteria,
            Status: Shared.Models.TaskStatus.Active,
            Type: request.Type,
            CurrentPhase: CollaborationPhase.Planning,
            CurrentPlan: currentPlan,
            ValidationStatus: WorkstreamStatus.Ready,
            ValidationSummary: "Pending reviewer and validator feedback.",
            ImplementationStatus: WorkstreamStatus.NotStarted,
            ImplementationSummary: "Implementation has not started yet.",
            PreferredRoles: preferredRoles,
            CreatedAt: now,
            UpdatedAt: now
        );

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

        // Persist the task
        var taskEntity = new TaskEntity
        {
            Id = task.Id,
            Title = task.Title,
            Description = task.Description,
            SuccessCriteria = task.SuccessCriteria,
            Status = task.Status.ToString(),
            Type = task.Type.ToString(),
            CurrentPhase = task.CurrentPhase.ToString(),
            CurrentPlan = task.CurrentPlan,
            ValidationStatus = task.ValidationStatus.ToString(),
            ValidationSummary = task.ValidationSummary,
            ImplementationStatus = task.ImplementationStatus.ToString(),
            ImplementationSummary = task.ImplementationSummary,
            PreferredRoles = JsonSerializer.Serialize(task.PreferredRoles),
            RoomId = roomEntity.Id,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Tasks.Add(taskEntity);

        // Add system messages for the task
        var assignmentMsg = CreateMessageEntity(
            roomEntity.Id, MessageKind.TaskAssignment,
            $"New task assigned: {request.Title}\n\n{request.Description}",
            correlationId, now);
        _db.Messages.Add(assignmentMsg);

        var planMsg = CreateMessageEntity(
            roomEntity.Id, MessageKind.Coordination,
            $"Phase set to Planning. Begin by reviewing requirements and proposing an approach.",
            correlationId, now);
        _db.Messages.Add(planMsg);

        // Publish activity events (adds entities to tracker before save)
        if (isNewRoom)
        {
            Publish(ActivityEventType.RoomCreated, roomEntity.Id, null, taskId,
                $"Room created for task: {request.Title}");
        }

        var activity = Publish(ActivityEventType.TaskCreated, roomEntity.Id, null, taskId,
            $"Task created: {request.Title}", correlationId);

        Publish(ActivityEventType.PhaseChanged, roomEntity.Id, null, taskId,
            "Phase changed to Planning");

        await _db.SaveChangesAsync();

        // Auto-join agents into the new task room so GetIdleAgentsInRoomAsync finds them.
        // Only move agents that aren't actively working (preserve breakout assignments).
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
    public async Task<List<TaskSnapshot>> GetTasksAsync()
    {
        var activeWorkspace = await GetActiveWorkspacePathAsync();
        var query = _db.Tasks.AsQueryable();

        if (activeWorkspace is not null)
        {
            // Keep historical tasks visible after workspace-scoped rooms were introduced:
            // legacy "main" tasks may retain a detached room row, and older task rooms may
            // no longer exist even though the task record is still valid.
            var workspaceRoomIds = await _db.Rooms
                .Where(r => r.WorkspacePath == activeWorkspace ||
                            (r.Id == _catalog.DefaultRoomId && r.WorkspacePath == null))
                .Select(r => r.Id)
                .ToListAsync();

            query = query.Where(t => t.RoomId != null &&
                                     (workspaceRoomIds.Contains(t.RoomId) ||
                                      !_db.Rooms.Any(r => r.Id == t.RoomId)));
        }

        var entities = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();
        return entities.Select(BuildTaskSnapshot).ToList();
    }

    /// <summary>
    /// Returns a specific task by ID, or null if not found.
    /// </summary>
    public async Task<TaskSnapshot?> GetTaskAsync(string taskId)
    {
        var entity = await _db.Tasks.FindAsync(taskId);
        if (entity is null) return null;
        var commentCount = await _db.TaskComments.CountAsync(c => c.TaskId == taskId);
        return BuildTaskSnapshot(entity, commentCount);
    }

    /// <summary>
    /// Assigns an agent to a task. Validates the agent exists in the catalog.
    /// </summary>
    public async Task<TaskSnapshot> AssignTaskAsync(string taskId, string agentId, string agentName)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var agent = _catalog.Agents.FirstOrDefault(a => a.Id == agentId);
        if (agent is not null)
        {
            entity.AssignedAgentId = agent.Id;
            entity.AssignedAgentName = agent.Name;
        }
        else
        {
            entity.AssignedAgentId = agentId;
            entity.AssignedAgentName = agentName;
        }

        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Updates a task's status. Automatically sets StartedAt/CompletedAt as appropriate.
    /// </summary>
    public async Task<TaskSnapshot> UpdateTaskStatusAsync(string taskId, Shared.Models.TaskStatus status)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");
        var now = DateTime.UtcNow;
        entity.Status = status.ToString();
        entity.UpdatedAt = now;

        if (status == Shared.Models.TaskStatus.Active && entity.StartedAt is null)
            entity.StartedAt = now;

        if (status == Shared.Models.TaskStatus.Completed || status == Shared.Models.TaskStatus.Cancelled)
            entity.CompletedAt = now;
        else
            entity.CompletedAt = null;

        await _db.SaveChangesAsync();
        return BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Records a branch name on a task.
    /// </summary>
    public async Task<TaskSnapshot> UpdateTaskBranchAsync(string taskId, string branchName)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");
        entity.BranchName = branchName;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Records PR information on a task.
    /// </summary>
    public async Task<TaskSnapshot> UpdateTaskPrAsync(
        string taskId, string url, int number, Shared.Models.PullRequestStatus status)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");
        entity.PullRequestUrl = url;
        entity.PullRequestNumber = number;
        entity.PullRequestStatus = status.ToString();
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Marks a task as complete with final metadata.
    /// </summary>
    public async Task<TaskSnapshot> CompleteTaskAsync(
        string taskId, int commitCount, List<string>? testsCreated = null, string? mergeCommitSha = null)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");
        var now = DateTime.UtcNow;
        entity.Status = nameof(Shared.Models.TaskStatus.Completed);
        entity.CompletedAt = now;
        entity.CommitCount = commitCount;
        if (testsCreated is not null)
            entity.TestsCreated = JsonSerializer.Serialize(testsCreated);
        entity.MergeCommitSha = mergeCommitSha;
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync();
        return BuildTaskSnapshot(entity);
    }

    // ── Task State Commands ──────────────────────────────────────

    /// <summary>
    /// Claims a task for an agent. Prevents double-claiming by another agent.
    /// Auto-activates tasks in Queued status.
    /// </summary>
    public async Task<TaskSnapshot> ClaimTaskAsync(string taskId, string agentId, string agentName)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        if (!string.IsNullOrEmpty(entity.AssignedAgentId) && entity.AssignedAgentId != agentId)
            throw new InvalidOperationException(
                $"Task '{taskId}' is already claimed by {entity.AssignedAgentName ?? entity.AssignedAgentId}");

        var agent = _catalog.Agents.FirstOrDefault(a => a.Id == agentId);
        entity.AssignedAgentId = agent?.Id ?? agentId;
        entity.AssignedAgentName = agent?.Name ?? agentName;

        var now = DateTime.UtcNow;
        entity.UpdatedAt = now;

        // Auto-activate queued tasks when claimed
        if (entity.Status == nameof(Shared.Models.TaskStatus.Queued))
        {
            entity.Status = nameof(Shared.Models.TaskStatus.Active);
            entity.StartedAt ??= now;
        }

        Publish(ActivityEventType.TaskClaimed, entity.RoomId, agentId, taskId,
            $"{entity.AssignedAgentName} claimed task: {Truncate(entity.Title, 80)}");

        await _db.SaveChangesAsync();
        return BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Releases a task claim. Only the currently assigned agent can release.
    /// </summary>
    public async Task<TaskSnapshot> ReleaseTaskAsync(string taskId, string agentId)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        if (string.IsNullOrEmpty(entity.AssignedAgentId))
            throw new InvalidOperationException(
                $"Task '{taskId}' is not currently claimed by any agent");

        if (entity.AssignedAgentId != agentId)
            throw new InvalidOperationException(
                $"Cannot release task '{taskId}' — claimed by {entity.AssignedAgentName ?? entity.AssignedAgentId}");

        var releasedName = entity.AssignedAgentName ?? agentId;
        entity.AssignedAgentId = null;
        entity.AssignedAgentName = null;
        entity.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.TaskReleased, entity.RoomId, agentId, taskId,
            $"{releasedName} released task: {Truncate(entity.Title, 80)}");

        await _db.SaveChangesAsync();
        return BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Approves a task after review. Records the reviewer and increments review rounds.
    /// </summary>
    public async Task<TaskSnapshot> ApproveTaskAsync(string taskId, string reviewerAgentId, string? findings = null)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var currentStatus = entity.Status;
        if (currentStatus != nameof(Shared.Models.TaskStatus.InReview) &&
            currentStatus != nameof(Shared.Models.TaskStatus.AwaitingValidation))
            throw new InvalidOperationException(
                $"Task '{taskId}' is in '{currentStatus}' state — must be InReview or AwaitingValidation to approve");

        var now = DateTime.UtcNow;
        entity.Status = nameof(Shared.Models.TaskStatus.Approved);
        entity.ReviewerAgentId = reviewerAgentId;
        entity.ReviewRounds++;
        entity.UpdatedAt = now;

        var reviewerName = _catalog.Agents.FirstOrDefault(a => a.Id == reviewerAgentId)?.Name ?? reviewerAgentId;

        if (!string.IsNullOrWhiteSpace(findings) && !string.IsNullOrEmpty(entity.RoomId))
        {
            var msgEntity = CreateMessageEntity(entity.RoomId, MessageKind.Review,
                $"✅ **Approved** by {reviewerName}\n\n{findings}", null, now);
            _db.Messages.Add(msgEntity);
        }

        Publish(ActivityEventType.TaskApproved, entity.RoomId, reviewerAgentId, taskId,
            $"{reviewerName} approved task: {Truncate(entity.Title, 80)}");

        await _db.SaveChangesAsync();
        return BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Requests changes on a task after review. Records the reviewer and increments review rounds.
    /// </summary>
    public async Task<TaskSnapshot> RequestChangesAsync(string taskId, string reviewerAgentId, string findings)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var currentStatus = entity.Status;
        if (currentStatus != nameof(Shared.Models.TaskStatus.InReview) &&
            currentStatus != nameof(Shared.Models.TaskStatus.AwaitingValidation))
            throw new InvalidOperationException(
                $"Task '{taskId}' is in '{currentStatus}' state — must be InReview or AwaitingValidation to request changes");

        var now = DateTime.UtcNow;
        entity.Status = nameof(Shared.Models.TaskStatus.ChangesRequested);
        entity.ReviewerAgentId = reviewerAgentId;
        entity.ReviewRounds++;
        entity.UpdatedAt = now;

        var reviewerName = _catalog.Agents.FirstOrDefault(a => a.Id == reviewerAgentId)?.Name ?? reviewerAgentId;

        if (!string.IsNullOrEmpty(entity.RoomId))
        {
            var msgEntity = CreateMessageEntity(entity.RoomId, MessageKind.Review,
                $"🔄 **Changes Requested** by {reviewerName}\n\n{findings}", null, now);
            _db.Messages.Add(msgEntity);
        }

        Publish(ActivityEventType.TaskChangesRequested, entity.RoomId, reviewerAgentId, taskId,
            $"{reviewerName} requested changes on task: {Truncate(entity.Title, 80)}");

        await _db.SaveChangesAsync();
        return BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Returns tasks that are pending review (InReview or AwaitingValidation).
    /// </summary>
    public async Task<List<TaskSnapshot>> GetReviewQueueAsync()
    {
        var reviewStatuses = new[]
        {
            nameof(Shared.Models.TaskStatus.InReview),
            nameof(Shared.Models.TaskStatus.AwaitingValidation)
        };

        var entities = await _db.Tasks
            .Where(t => reviewStatuses.Contains(t.Status))
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        return entities.Select(BuildTaskSnapshot).ToList();
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
        var task = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var comment = new TaskCommentEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            AgentId = agentId,
            AgentName = agentName,
            CommentType = commentType.ToString(),
            Content = content,
            CreatedAt = DateTime.UtcNow
        };
        _db.TaskComments.Add(comment);

        Publish(ActivityEventType.TaskCommentAdded, task.RoomId, agentId, taskId,
            $"{agentName} added {commentType.ToString().ToLower()} on task: {task.Title}");

        await _db.SaveChangesAsync();

        return BuildTaskComment(comment);
    }

    /// <summary>
    /// Gets all comments for a task, ordered by creation time.
    /// </summary>
    public async Task<List<TaskComment>> GetTaskCommentsAsync(string taskId)
    {
        var task = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var comments = await _db.TaskComments
            .Where(c => c.TaskId == taskId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

        return comments.Select(BuildTaskComment).ToList();
    }

    /// <summary>
    /// Gets the count of comments for a task.
    /// </summary>
    public async Task<int> GetTaskCommentCountAsync(string taskId)
    {
        return await _db.TaskComments.CountAsync(c => c.TaskId == taskId);
    }

    private static TaskComment BuildTaskComment(TaskCommentEntity entity)
    {
        return new TaskComment(
            Id: entity.Id,
            TaskId: entity.TaskId,
            AgentId: entity.AgentId,
            AgentName: entity.AgentName,
            CommentType: Enum.TryParse<TaskCommentType>(entity.CommentType, out var ct) ? ct : TaskCommentType.Comment,
            Content: entity.Content,
            CreatedAt: entity.CreatedAt
        );
    }

    // ── Message Management ──────────────────────────────────────

    /// <summary>
    /// Posts an agent message to a room.
    /// </summary>
    public async Task<ChatEnvelope> PostMessageAsync(PostMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RoomId))
            throw new ArgumentException("RoomId is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.SenderId))
            throw new ArgumentException("SenderId is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Content))
            throw new ArgumentException("Content is required", nameof(request));

        var room = await _db.Rooms.FindAsync(request.RoomId)
            ?? throw new InvalidOperationException($"Room '{request.RoomId}' not found");

        var agent = _catalog.Agents.FirstOrDefault(a => a.Id == request.SenderId)
            ?? throw new InvalidOperationException($"Agent '{request.SenderId}' not found in catalog");

        var now = DateTime.UtcNow;
        var envelope = new ChatEnvelope(
            Id: Guid.NewGuid().ToString("N"),
            RoomId: request.RoomId,
            SenderId: agent.Id,
            SenderName: agent.Name,
            SenderRole: agent.Role,
            SenderKind: MessageSenderKind.Agent,
            Kind: request.Kind,
            Content: request.Content,
            SentAt: now,
            CorrelationId: request.CorrelationId,
            Hint: request.Hint
        );

        var msgEntity = new MessageEntity
        {
            Id = envelope.Id,
            RoomId = envelope.RoomId,
            SenderId = envelope.SenderId,
            SenderName = envelope.SenderName,
            SenderRole = envelope.SenderRole,
            SenderKind = envelope.SenderKind.ToString(),
            Kind = envelope.Kind.ToString(),
            Content = envelope.Content,
            SentAt = envelope.SentAt,
            CorrelationId = envelope.CorrelationId
        };

        // Tag message with active conversation session
        var session = await _sessionService.GetOrCreateActiveSessionAsync(request.RoomId);
        msgEntity.SessionId = session.Id;

        _db.Messages.Add(msgEntity);
        await _sessionService.IncrementMessageCountAsync(session.Id);

        // Trim to last MaxRecentMessages
        await TrimMessagesAsync(request.RoomId);

        room.UpdatedAt = now;

        Publish(ActivityEventType.MessagePosted, request.RoomId, agent.Id, null,
            $"{agent.Name}: {request.Content}");

        await _db.SaveChangesAsync();

        return envelope;
    }

    /// <summary>
    /// Posts a human message to a room.
    /// When identity parameters are provided, the message is attributed to that user.
    /// Otherwise falls back to generic "Human" identity.
    /// </summary>
    public async Task<ChatEnvelope> PostHumanMessageAsync(
        string roomId, string content,
        string? userId = null, string? userName = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new ArgumentException("roomId is required", nameof(roomId));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("content is required", nameof(content));

        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found");

        var senderId = userId ?? "human";
        var senderName = userName ?? "Human";

        var now = DateTime.UtcNow;
        var envelope = new ChatEnvelope(
            Id: Guid.NewGuid().ToString("N"),
            RoomId: roomId,
            SenderId: senderId,
            SenderName: senderName,
            SenderRole: "Human",
            SenderKind: MessageSenderKind.User,
            Kind: MessageKind.Response,
            Content: content,
            SentAt: now
        );

        var msgEntity = new MessageEntity
        {
            Id = envelope.Id,
            RoomId = roomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderRole = "Human",
            SenderKind = nameof(MessageSenderKind.User),
            Kind = nameof(MessageKind.Response),
            Content = content,
            SentAt = now
        };

        // Tag message with active conversation session
        var session = await _sessionService.GetOrCreateActiveSessionAsync(roomId);
        msgEntity.SessionId = session.Id;

        _db.Messages.Add(msgEntity);
        await _sessionService.IncrementMessageCountAsync(session.Id);

        await TrimMessagesAsync(roomId);

        room.UpdatedAt = now;

        // ActorId stays "human" for activity event filtering (broadcaster suppresses echo)
        Publish(ActivityEventType.MessagePosted, roomId, "human", null,
            $"{senderName}: {content}");

        await _db.SaveChangesAsync();

        return envelope;
    }

    // ── Direct Messaging ────────────────────────────────────────

    /// <summary>
    /// Stores a direct message and posts a system notification in the recipient's room.
    /// </summary>
    public async Task<string> SendDirectMessageAsync(
        string senderId, string senderName, string senderRole,
        string recipientId, string message, string currentRoomId)
    {
        var now = DateTime.UtcNow;
        var messageId = Guid.NewGuid().ToString("N");

        var msgEntity = new MessageEntity
        {
            Id = messageId,
            RoomId = currentRoomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderRole = senderRole,
            SenderKind = senderId == "human" ? nameof(MessageSenderKind.User) : nameof(MessageSenderKind.Agent),
            Kind = nameof(MessageKind.DirectMessage),
            Content = message,
            SentAt = now,
            RecipientId = recipientId
        };
        _db.Messages.Add(msgEntity);

        // Post system notification in recipient's current room (audit metadata, no content)
        if (recipientId != "human")
        {
            var recipientLocation = await _db.AgentLocations.FindAsync(recipientId);
            var notifyRoomId = recipientLocation?.RoomId ?? currentRoomId;
            var notifyRoom = await _db.Rooms.FindAsync(notifyRoomId);
            if (notifyRoom is not null)
            {
                var sysMsg = new MessageEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    RoomId = notifyRoomId,
                    SenderId = "system",
                    SenderName = "System",
                    SenderKind = nameof(MessageSenderKind.System),
                    Kind = nameof(MessageKind.System),
                    Content = $"📩 {senderName} sent a direct message to {recipientId}.",
                    SentAt = now
                };
                _db.Messages.Add(sysMsg);
                notifyRoom.UpdatedAt = now;
            }
        }

        Publish(ActivityEventType.DirectMessageSent, currentRoomId, senderId, null,
            $"DM from {senderName} to {recipientId}");

        await _db.SaveChangesAsync();
        return messageId;
    }

    /// <summary>
    /// Returns recent DMs for an agent (both sent and received), ordered chronologically.
    /// </summary>
    public async Task<List<MessageEntity>> GetDirectMessagesForAgentAsync(string agentId, int limit = 20)
    {
        return await _db.Messages
            .Where(m => m.RecipientId != null &&
                        (m.RecipientId == agentId || m.SenderId == agentId))
            .OrderByDescending(m => m.SentAt)
            .Take(limit)
            .OrderBy(m => m.SentAt)
            .ToListAsync();
    }

    /// <summary>
    /// Returns DM thread summaries for the human user, grouped by agent.
    /// </summary>
    public async Task<List<DmThreadSummary>> GetDmThreadsForHumanAsync()
    {
        var humanDms = await _db.Messages
            .Where(m => m.RecipientId != null &&
                        (m.RecipientId == "human" || m.SenderId == "human"))
            .OrderByDescending(m => m.SentAt)
            .Take(500) // Cap to prevent unbounded scans
            .ToListAsync();

        var threads = new Dictionary<string, DmThreadSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var dm in humanDms)
        {
            var agentId = dm.SenderId == "human" ? dm.RecipientId! : dm.SenderId;

            if (!threads.ContainsKey(agentId))
            {
                // Find agent name from catalog
                var agent = _catalog.Agents.FirstOrDefault(
                    a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));
                var agentName = agent?.Name ?? agentId;
                var agentRole = agent?.Role ?? "Agent";

                threads[agentId] = new DmThreadSummary(
                    AgentId: agentId,
                    AgentName: agentName,
                    AgentRole: agentRole,
                    LastMessage: dm.Content.Length > 100 ? dm.Content[..100] + "…" : dm.Content,
                    LastMessageAt: dm.SentAt,
                    MessageCount: 0
                );
            }

            threads[agentId] = threads[agentId] with
            {
                MessageCount = threads[agentId].MessageCount + 1
            };
        }

        return threads.Values
            .OrderByDescending(t => t.LastMessageAt)
            .ToList();
    }

    /// <summary>
    /// Returns messages in a DM thread between the human and a specific agent.
    /// </summary>
    public async Task<List<MessageEntity>> GetDmThreadMessagesAsync(string agentId, int limit = 50)
    {
        return await _db.Messages
            .Where(m => m.RecipientId != null &&
                        ((m.SenderId == "human" && m.RecipientId == agentId) ||
                         (m.SenderId == agentId && m.RecipientId == "human")))
            .OrderByDescending(m => m.SentAt)
            .Take(limit)
            .OrderBy(m => m.SentAt)
            .ToListAsync();
    }

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
        if (_catalog.Agents.All(a => a.Id != agentId))
            throw new InvalidOperationException($"Agent '{agentId}' not found in catalog");

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

    /// <summary>
    /// Creates a breakout room and moves the assigned agent to "Working" state.
    /// </summary>
    public async Task<BreakoutRoom> CreateBreakoutRoomAsync(
        string parentRoomId, string agentId, string name)
    {
        var parentRoom = await _db.Rooms.FindAsync(parentRoomId)
            ?? throw new InvalidOperationException($"Parent room '{parentRoomId}' not found");

        var agent = _catalog.Agents.FirstOrDefault(a => a.Id == agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found in catalog");

        var now = DateTime.UtcNow;
        var breakoutId = Guid.NewGuid().ToString("N");

        var entity = new BreakoutRoomEntity
        {
            Id = breakoutId,
            Name = name,
            ParentRoomId = parentRoomId,
            AssignedAgentId = agentId,
            Status = nameof(RoomStatus.Active),
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.BreakoutRooms.Add(entity);

        // Move agent to working state
        await MoveAgentAsync(agentId, parentRoomId, AgentState.Working, breakoutId);

        Publish(ActivityEventType.RoomCreated, parentRoomId, agentId, null,
            $"Breakout room created: {name}");

        await _db.SaveChangesAsync();

        return new BreakoutRoom(
            Id: breakoutId,
            Name: name,
            ParentRoomId: parentRoomId,
            AssignedAgentId: agentId,
            Tasks: [],
            Status: RoomStatus.Active,
            RecentMessages: [],
            CreatedAt: now,
            UpdatedAt: now
        );
    }

    /// <summary>
    /// Closes a breakout room and moves the agent back to idle.
    /// </summary>
    public async Task CloseBreakoutRoomAsync(
        string breakoutId,
        BreakoutRoomCloseReason closeReason = BreakoutRoomCloseReason.Completed)
    {
        var entity = await _db.BreakoutRooms.FindAsync(breakoutId)
            ?? throw new InvalidOperationException($"Breakout room '{breakoutId}' not found");

        if (TerminalBreakoutStatuses.Contains(entity.Status))
            return; // Already archived — no-op to prevent corrupting agent state

        // Move agent back to idle in parent room
        await MoveAgentAsync(entity.AssignedAgentId, entity.ParentRoomId, AgentState.Idle);

        // Soft-delete: archive instead of removing (preserves messages for history)
        entity.Status = nameof(RoomStatus.Archived);
        entity.CloseReason = closeReason.ToString();
        entity.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.RoomClosed, entity.ParentRoomId, entity.AssignedAgentId, null,
            $"Breakout room closed: {entity.Name} ({closeReason})");

        await _db.SaveChangesAsync();
    }

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

        await _db.SaveChangesAsync();

        _logger.LogWarning(
            "Crash recovery completed for room {RoomId}: closed {BreakoutCount} breakouts, reset {AgentCount} stuck agents, reset {TaskCount} stuck tasks",
            mainRoomId, activeBreakoutIds.Count, lingeringWorkingAgents.Count, recoverableTasks.Count);

        return new CrashRecoveryResult(activeBreakoutIds.Count, lingeringWorkingAgents.Count, recoverableTasks.Count);
    }

    /// <summary>
    /// Returns a single breakout room by its ID, or null if not found.
    /// </summary>
    public async Task<BreakoutRoom?> GetBreakoutRoomAsync(string breakoutId)
    {
        var entity = await _db.BreakoutRooms
            .Include(br => br.Messages)
            .FirstOrDefaultAsync(br => br.Id == breakoutId);

        return entity is null ? null : BuildBreakoutRoomSnapshot(entity);
    }

    /// <summary>
    /// Returns breakout rooms for a given parent room.
    /// </summary>
    public async Task<List<BreakoutRoom>> GetBreakoutRoomsAsync(string parentRoomId)
    {
        var entities = await _db.BreakoutRooms
            .Include(br => br.Messages)
            .Where(br => br.ParentRoomId == parentRoomId && br.Status == nameof(RoomStatus.Active))
            .ToListAsync();

        return entities.Select(BuildBreakoutRoomSnapshot).ToList();
    }

    /// <summary>
    /// Links a breakout room to a TaskEntity for reliable lookup during completion.
    /// </summary>
    public async Task SetBreakoutTaskIdAsync(string breakoutRoomId, string taskId)
    {
        var entity = await _db.BreakoutRooms.FindAsync(breakoutRoomId)
            ?? throw new InvalidOperationException($"Breakout room '{breakoutRoomId}' not found");
        entity.TaskId = taskId;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns the TaskEntity ID linked to a breakout room, or null if none.
    /// </summary>
    public async Task<string?> GetBreakoutTaskIdAsync(string breakoutRoomId)
    {
        var entity = await _db.BreakoutRooms.FindAsync(breakoutRoomId);
        return entity?.TaskId;
    }

    /// <summary>
    /// Moves the task linked to a breakout room into InReview status.
    /// Returns the updated task, or null when the breakout has no linked task.
    /// </summary>
    public async Task<TaskSnapshot?> TransitionBreakoutTaskToInReviewAsync(string breakoutRoomId)
    {
        var taskId = await GetBreakoutTaskIdAsync(breakoutRoomId);
        if (string.IsNullOrWhiteSpace(taskId))
            return null;

        return await UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.InReview);
    }

    /// <summary>
    /// Ensures a TaskEntity exists for a breakout assignment.
    /// Searches by title match, then room+status, then agent+status,
    /// then any unassigned task without a branch.
    /// Creates a new TaskEntity if none found.
    /// Returns the TaskEntity ID.
    /// </summary>
    public async Task<string> EnsureTaskForBreakoutAsync(
        string title, string description, string agentId, string roomId, string? currentPlan = null)
    {
        var tasks = await _db.Tasks.ToListAsync();

        // 1. Exact title match (case-insensitive)
        var match = tasks.FirstOrDefault(t =>
            t.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

        // 2. Same room + Active/Queued status
        match ??= tasks.FirstOrDefault(t =>
            t.RoomId == roomId &&
            (t.Status == nameof(Shared.Models.TaskStatus.Active) ||
             t.Status == nameof(Shared.Models.TaskStatus.Queued)));

        // 3. Same agent + Active/Queued status
        match ??= tasks.FirstOrDefault(t =>
            t.AssignedAgentId == agentId &&
            (t.Status == nameof(Shared.Models.TaskStatus.Active) ||
             t.Status == nameof(Shared.Models.TaskStatus.Queued)));

        // 4. Any Active/Queued task without a branch (likely created by
        //    CREATE_TASK in a different room and not yet assigned to a breakout).
        //    Only use this fallback if there's exactly one candidate to avoid
        //    picking the wrong task when multiple are pending.
        if (match is null)
        {
            var unassigned = tasks.Where(t =>
                string.IsNullOrEmpty(t.BranchName) &&
                (t.Status == nameof(Shared.Models.TaskStatus.Active) ||
                 t.Status == nameof(Shared.Models.TaskStatus.Queued)))
                .ToList();

            if (unassigned.Count == 1)
            {
                match = unassigned[0];
                _logger.LogInformation(
                    "Matched breakout \"{Title}\" to existing task \"{TaskTitle}\" (sole unassigned task)",
                    title, match.Title);
            }
            else if (unassigned.Count > 1)
            {
                _logger.LogWarning(
                    "Found {Count} unassigned tasks — cannot auto-match breakout \"{Title}\". Creating new TaskEntity.",
                    unassigned.Count, title);
            }
        }

        if (match is not null)
            return match.Id;

        // Create a new TaskEntity for this breakout assignment
        var now = DateTime.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var agent = _catalog.Agents.FirstOrDefault(a => a.Id == agentId);

        var entity = new TaskEntity
        {
            Id = taskId,
            Title = title,
            Description = description,
            SuccessCriteria = "",
            Status = nameof(Shared.Models.TaskStatus.Active),
            Type = nameof(TaskType.Feature),
            CurrentPhase = nameof(CollaborationPhase.Implementation),
            CurrentPlan = ResolveTaskPlanContent(title, currentPlan),
            ValidationStatus = nameof(WorkstreamStatus.NotStarted),
            ValidationSummary = "",
            ImplementationStatus = nameof(WorkstreamStatus.InProgress),
            ImplementationSummary = "",
            PreferredRoles = "[]",
            RoomId = roomId,
            AssignedAgentId = agentId,
            AssignedAgentName = agent?.Name,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Tasks.Add(entity);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created TaskEntity {TaskId} for breakout assignment: {Title}", taskId, title);
        return taskId;
    }

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
        return _activityBus.GetRecentActivity();
    }

    /// <summary>
    /// Subscribes to activity events. Returns an unsubscribe action.
    /// </summary>
    public Action StreamActivity(Action<ActivityEvent> callback)
    {
        return _activityBus.Subscribe(callback);
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
    {
        var evt = new ActivityEvent(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            Severity: severity,
            RoomId: roomId,
            ActorId: actorId,
            TaskId: taskId,
            Message: message,
            CorrelationId: correlationId,
            OccurredAt: DateTime.UtcNow
        );

        // Add to EF tracker (caller must call SaveChangesAsync)
        _db.ActivityEvents.Add(new ActivityEventEntity
        {
            Id = evt.Id,
            Type = evt.Type.ToString(),
            Severity = evt.Severity.ToString(),
            RoomId = evt.RoomId,
            ActorId = evt.ActorId,
            TaskId = evt.TaskId,
            Message = evt.Message,
            CorrelationId = evt.CorrelationId,
            OccurredAt = evt.OccurredAt
        });

        // Broadcast via singleton (in-memory buffer + subscribers)
        _activityBus.Broadcast(evt);

        return evt;
    }

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
    {
        return new TaskSnapshot(
            Id: entity.Id,
            Title: entity.Title,
            Description: entity.Description,
            SuccessCriteria: entity.SuccessCriteria,
            Status: Enum.Parse<Shared.Models.TaskStatus>(entity.Status),
            Type: Enum.TryParse<TaskType>(entity.Type, out var tt) ? tt : TaskType.Feature,
            CurrentPhase: Enum.Parse<CollaborationPhase>(entity.CurrentPhase),
            CurrentPlan: entity.CurrentPlan,
            ValidationStatus: Enum.Parse<WorkstreamStatus>(entity.ValidationStatus),
            ValidationSummary: entity.ValidationSummary,
            ImplementationStatus: Enum.Parse<WorkstreamStatus>(entity.ImplementationStatus),
            ImplementationSummary: entity.ImplementationSummary,
            PreferredRoles: JsonSerializer.Deserialize<List<string>>(entity.PreferredRoles) ?? [],
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            Size: string.IsNullOrEmpty(entity.Size) ? null : Enum.Parse<TaskSize>(entity.Size),
            StartedAt: entity.StartedAt,
            CompletedAt: entity.CompletedAt,
            AssignedAgentId: entity.AssignedAgentId,
            AssignedAgentName: entity.AssignedAgentName,
            UsedFleet: entity.UsedFleet,
            FleetModels: JsonSerializer.Deserialize<List<string>>(entity.FleetModels) ?? [],
            BranchName: entity.BranchName,
            PullRequestUrl: entity.PullRequestUrl,
            PullRequestNumber: entity.PullRequestNumber,
            PullRequestStatus: string.IsNullOrEmpty(entity.PullRequestStatus) ? null : Enum.Parse<Shared.Models.PullRequestStatus>(entity.PullRequestStatus),
            ReviewerAgentId: entity.ReviewerAgentId,
            ReviewRounds: entity.ReviewRounds,
            TestsCreated: JsonSerializer.Deserialize<List<string>>(entity.TestsCreated) ?? [],
            CommitCount: entity.CommitCount,
            MergeCommitSha: entity.MergeCommitSha,
            CommentCount: commentCount
        );
    }

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

    private static string ResolveTaskPlanContent(string title, string? currentPlan)
    {
        if (!string.IsNullOrWhiteSpace(currentPlan))
            return currentPlan.Trim();

        return $"# {title}\n\n## Plan\n1. Review requirements\n2. Design solution\n3. Implement\n4. Validate";
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

    private BreakoutRoom BuildBreakoutRoomSnapshot(BreakoutRoomEntity entity)
    {
        // Filter messages to active session if one exists.
        // Since breakout rooms are short-lived, most won't have session boundaries,
        // but this handles long-running breakouts that exceed the threshold.
        var activeSession = _db.ConversationSessions
            .Where(s => s.RoomId == entity.Id && s.Status == "Active")
            .FirstOrDefault();
        var activeSessionId = activeSession?.Id;

        var filteredMessages = entity.Messages?
            .Where(m => activeSessionId == null || m.SessionId == activeSessionId || m.SessionId == null)
            .OrderBy(m => m.SentAt)
            ?? Enumerable.Empty<BreakoutMessageEntity>();

        return new BreakoutRoom(
            Id: entity.Id,
            Name: entity.Name,
            ParentRoomId: entity.ParentRoomId,
            AssignedAgentId: entity.AssignedAgentId,
            Tasks: [],
            Status: Enum.Parse<RoomStatus>(entity.Status),
            RecentMessages: filteredMessages
                .Select(m => new ChatEnvelope(
                Id: m.Id,
                RoomId: entity.ParentRoomId,
                SenderId: m.SenderId,
                SenderName: m.SenderName,
                SenderRole: m.SenderRole,
                SenderKind: Enum.Parse<MessageSenderKind>(m.SenderKind),
                Kind: Enum.Parse<MessageKind>(m.Kind),
                Content: m.Content,
                SentAt: m.SentAt
            )).ToList(),
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt
        );
    }

    private async Task<List<BreakoutRoom>> GetAllBreakoutRoomsAsync()
    {
        var entities = await _db.BreakoutRooms
            .Include(br => br.Messages)
            .Where(br => br.Status == nameof(RoomStatus.Active))
            .ToListAsync();
        return entities.Select(BuildBreakoutRoomSnapshot).ToList();
    }

    /// <summary>
    /// Returns all breakout rooms (active and archived) assigned to a specific agent,
    /// ordered by most recent first. Used for agent session history.
    /// </summary>
    public async Task<List<BreakoutRoom>> GetAgentSessionsAsync(string agentId)
    {
        var activeWorkspace = await GetActiveWorkspacePathAsync();
        var query = _db.BreakoutRooms
            .Include(br => br.Messages)
            .Where(br => br.AssignedAgentId == agentId);

        if (activeWorkspace is not null)
        {
            var workspaceRoomIds = await _db.Rooms
                .Where(r => r.WorkspacePath == activeWorkspace)
                .Select(r => r.Id)
                .ToListAsync();
            query = query.Where(br => workspaceRoomIds.Contains(br.ParentRoomId));
        }

        var entities = await query
            .OrderByDescending(br => br.UpdatedAt)
            .ToListAsync();
        return entities.Select(BuildBreakoutRoomSnapshot).ToList();
    }

    private MessageEntity CreateMessageEntity(
        string roomId, MessageKind kind, string content,
        string? correlationId, DateTime sentAt)
    {
        return new MessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RoomId = roomId,
            SenderId = "system",
            SenderName = "System",
            SenderKind = nameof(MessageSenderKind.System),
            Kind = kind.ToString(),
            Content = content,
            SentAt = sentAt,
            CorrelationId = correlationId
        };
    }

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

    private async Task TrimMessagesAsync(string roomId)
    {
        // Count committed room messages only (exclude DMs, pending tracked add is +1)
        var messageCount = await _db.Messages.CountAsync(m => m.RoomId == roomId && m.RecipientId == null);
        var totalAfterSave = messageCount + 1; // account for the pending message

        if (totalAfterSave <= MaxRecentMessages) return;

        var toRemove = await _db.Messages
            .Where(m => m.RoomId == roomId && m.RecipientId == null)
            .OrderBy(m => m.SentAt)
            .Take(totalAfterSave - MaxRecentMessages)
            .ToListAsync();

        _db.Messages.RemoveRange(toRemove);
    }

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
    public async Task PostSystemStatusAsync(string roomId, string message)
    {
        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found");

        var now = DateTime.UtcNow;
        var entity = CreateMessageEntity(roomId, MessageKind.System, message, null, now);
        _db.Messages.Add(entity);
        room.UpdatedAt = now;

        Publish(ActivityEventType.MessagePosted, roomId, null, null,
            $"System: {Truncate(message, 100)}");

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds a message to a breakout room's message log.
    /// </summary>
    public async Task PostBreakoutMessageAsync(
        string breakoutRoomId, string senderId, string senderName,
        string senderRole, string content)
    {
        var br = await _db.BreakoutRooms.FindAsync(breakoutRoomId)
            ?? throw new InvalidOperationException($"Breakout room '{breakoutRoomId}' not found");

        if (br.Status != nameof(RoomStatus.Active))
            throw new InvalidOperationException($"Breakout room '{breakoutRoomId}' is archived");

        var now = DateTime.UtcNow;
        var entity = new BreakoutMessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            BreakoutRoomId = breakoutRoomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderRole = senderRole,
            SenderKind = senderId == "system"
                ? nameof(MessageSenderKind.System)
                : nameof(MessageSenderKind.Agent),
            Kind = senderId == "system"
                ? nameof(MessageKind.System)
                : nameof(MessageKind.Response),
            Content = content,
            SentAt = now
        };

        // Tag message with active conversation session for breakout room
        var session = await _sessionService.GetOrCreateActiveSessionAsync(breakoutRoomId, "Breakout");
        entity.SessionId = session.Id;

        _db.BreakoutMessages.Add(entity);
        await _sessionService.IncrementMessageCountAsync(session.Id);

        br.UpdatedAt = now;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Creates a task item associated with a breakout room.
    /// </summary>
    public async Task<TaskItem> CreateTaskItemAsync(
        string title, string description, string assignedTo,
        string roomId, string? breakoutRoomId)
    {
        var now = DateTime.UtcNow;
        var entity = new TaskItemEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Description = description,
            Status = nameof(TaskItemStatus.Pending),
            AssignedTo = assignedTo,
            RoomId = roomId,
            BreakoutRoomId = breakoutRoomId,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.TaskItems.Add(entity);
        await _db.SaveChangesAsync();

        return new TaskItem(
            entity.Id, entity.Title, entity.Description,
            TaskItemStatus.Pending, entity.AssignedTo,
            entity.RoomId, entity.BreakoutRoomId,
            null, null, now, now);
    }

    /// <summary>
    /// Updates the status of a task item, with optional evidence.
    /// </summary>
    public async Task UpdateTaskItemStatusAsync(
        string taskItemId, TaskItemStatus status, string? evidence = null)
    {
        var entity = await _db.TaskItems.FindAsync(taskItemId);
        if (entity is null) return; // Silently ignore — matches v1 behavior

        entity.Status = status.ToString();
        entity.UpdatedAt = DateTime.UtcNow;
        if (evidence is not null) entity.Evidence = evidence;

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns task items associated with a breakout room.
    /// </summary>
    public async Task<List<TaskItem>> GetBreakoutTaskItemsAsync(string breakoutRoomId)
    {
        var entities = await _db.TaskItems
            .Where(t => t.BreakoutRoomId == breakoutRoomId)
            .ToListAsync();

        return entities.Select(e => new TaskItem(
            e.Id, e.Title, e.Description,
            Enum.Parse<TaskItemStatus>(e.Status),
            e.AssignedTo, e.RoomId, e.BreakoutRoomId,
            e.Evidence, e.Feedback,
            e.CreatedAt, e.UpdatedAt)).ToList();
    }

    /// <summary>
    /// Returns all task items that are not done (Pending or Active).
    /// </summary>
    public async Task<List<TaskItem>> GetActiveTaskItemsAsync()
    {
        var activeWorkspace = await GetActiveWorkspacePathAsync();
        var activeStatuses = new[] { nameof(TaskItemStatus.Pending), nameof(TaskItemStatus.Active) };
        var query = _db.TaskItems
            .Where(t => activeStatuses.Contains(t.Status));

        if (activeWorkspace is not null)
        {
            var workspaceRoomIds = await _db.Rooms
                .Where(r => r.WorkspacePath == activeWorkspace)
                .Select(r => r.Id)
                .ToListAsync();
            query = query.Where(t => workspaceRoomIds.Contains(t.RoomId));
        }

        var entities = await query.OrderBy(t => t.CreatedAt).ToListAsync();

        return entities.Select(e => new TaskItem(
            e.Id, e.Title, e.Description,
            Enum.Parse<TaskItemStatus>(e.Status),
            e.AssignedTo, e.RoomId, e.BreakoutRoomId,
            e.Evidence, e.Feedback,
            e.CreatedAt, e.UpdatedAt)).ToList();
    }
}
