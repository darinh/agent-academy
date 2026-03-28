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

    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<WorkspaceRuntime> _logger;
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityBroadcaster _activityBus;

    public WorkspaceRuntime(
        AgentAcademyDbContext db,
        ILogger<WorkspaceRuntime> logger,
        AgentCatalogOptions catalog,
        ActivityBroadcaster activityBus)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activityBus = activityBus;
    }

    // ── Initialization ──────────────────────────────────────────

    /// <summary>
    /// Ensures the default room and agent locations exist.
    /// Call once at startup within a scope.
    /// </summary>
    public async Task InitializeAsync()
    {
        var defaultRoomId = _catalog.DefaultRoomId;
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

            // Add a system welcome message
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
    /// Returns all rooms as snapshots, ordered by name.
    /// </summary>
    public async Task<List<RoomSnapshot>> GetRoomsAsync()
    {
        var rooms = await _db.Rooms
            .OrderBy(r => r.Name)
            .ToListAsync();

        var snapshots = new List<RoomSnapshot>();
        foreach (var room in rooms)
        {
            snapshots.Add(await BuildRoomSnapshotAsync(room));
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

        var task = new TaskSnapshot(
            Id: taskId,
            Title: request.Title,
            Description: request.Description,
            SuccessCriteria: request.SuccessCriteria,
            Status: Shared.Models.TaskStatus.Active,
            CurrentPhase: CollaborationPhase.Planning,
            CurrentPlan: $"Plan for: {request.Title}\n\n1. Review requirements\n2. Design solution\n3. Implement\n4. Validate",
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

            roomEntity = new RoomEntity
            {
                Id = roomId,
                Name = request.Title,
                Status = nameof(RoomStatus.Active),
                CurrentPhase = nameof(CollaborationPhase.Planning),
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
        var entities = await _db.Tasks.OrderByDescending(t => t.CreatedAt).ToListAsync();
        return entities.Select(BuildTaskSnapshot).ToList();
    }

    /// <summary>
    /// Returns a specific task by ID, or null if not found.
    /// </summary>
    public async Task<TaskSnapshot?> GetTaskAsync(string taskId)
    {
        var entity = await _db.Tasks.FindAsync(taskId);
        return entity is null ? null : BuildTaskSnapshot(entity);
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
        string taskId, int commitCount, List<string>? testsCreated = null)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");
        var now = DateTime.UtcNow;
        entity.Status = nameof(Shared.Models.TaskStatus.Completed);
        entity.CompletedAt = now;
        entity.CommitCount = commitCount;
        if (testsCreated is not null)
            entity.TestsCreated = JsonSerializer.Serialize(testsCreated);
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync();
        return BuildTaskSnapshot(entity);
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
        _db.Messages.Add(msgEntity);

        // Trim to last MaxRecentMessages
        await TrimMessagesAsync(request.RoomId);

        room.UpdatedAt = now;

        Publish(ActivityEventType.MessagePosted, request.RoomId, agent.Id, null,
            $"{agent.Name}: {Truncate(request.Content, 100)}");

        await _db.SaveChangesAsync();

        return envelope;
    }

    /// <summary>
    /// Posts a human message to a room.
    /// </summary>
    public async Task<ChatEnvelope> PostHumanMessageAsync(string roomId, string content)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new ArgumentException("roomId is required", nameof(roomId));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("content is required", nameof(content));

        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found");

        var now = DateTime.UtcNow;
        var envelope = new ChatEnvelope(
            Id: Guid.NewGuid().ToString("N"),
            RoomId: roomId,
            SenderId: "human",
            SenderName: "You",
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
            SenderId = "human",
            SenderName = "You",
            SenderRole = "Human",
            SenderKind = nameof(MessageSenderKind.User),
            Kind = nameof(MessageKind.Response),
            Content = content,
            SentAt = now
        };
        _db.Messages.Add(msgEntity);

        await TrimMessagesAsync(roomId);

        room.UpdatedAt = now;

        Publish(ActivityEventType.MessagePosted, roomId, "human", null,
            $"You: {Truncate(content, 100)}");

        await _db.SaveChangesAsync();

        return envelope;
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
    public async Task CloseBreakoutRoomAsync(string breakoutId)
    {
        var entity = await _db.BreakoutRooms.FindAsync(breakoutId)
            ?? throw new InvalidOperationException($"Breakout room '{breakoutId}' not found");

        // Move agent back to idle in parent room
        await MoveAgentAsync(entity.AssignedAgentId, entity.ParentRoomId, AgentState.Idle);

        // Remove breakout messages
        var messages = await _db.BreakoutMessages
            .Where(m => m.BreakoutRoomId == breakoutId)
            .ToListAsync();
        _db.BreakoutMessages.RemoveRange(messages);

        // Remove breakout room
        _db.BreakoutRooms.Remove(entity);

        Publish(ActivityEventType.RoomClosed, entity.ParentRoomId, entity.AssignedAgentId, null,
            $"Breakout room closed: {entity.Name}");

        await _db.SaveChangesAsync();
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
            .Where(br => br.ParentRoomId == parentRoomId)
            .ToListAsync();

        return entities.Select(BuildBreakoutRoomSnapshot).ToList();
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

    private async Task<RoomSnapshot> BuildRoomSnapshotAsync(RoomEntity room)
    {
        // Get recent messages (last MaxRecentMessages)
        var messages = await _db.Messages
            .Where(m => m.RoomId == room.Id)
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

        // Build participants from configured agents
        var participants = BuildParticipants(
            activeTask?.PreferredRoles ?? [],
            room.UpdatedAt);

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
        List<string> preferredRoles, DateTime observedAt)
    {
        return _catalog.Agents
            .Where(a => a.AutoJoinDefaultRoom || preferredRoles.Contains(a.Role))
            .Select(a => new AgentPresence(
                AgentId: a.Id,
                Name: a.Name,
                Role: a.Role,
                Availability: preferredRoles.Contains(a.Role)
                    ? AgentAvailability.Preferred
                    : AgentAvailability.Ready,
                IsPreferred: preferredRoles.Contains(a.Role),
                LastActivityAt: observedAt,
                ActiveCapabilities: [.. a.CapabilityTags]
            ))
            .ToList();
    }

    private static TaskSnapshot BuildTaskSnapshot(TaskEntity entity)
    {
        return new TaskSnapshot(
            Id: entity.Id,
            Title: entity.Title,
            Description: entity.Description,
            SuccessCriteria: entity.SuccessCriteria,
            Status: Enum.Parse<Shared.Models.TaskStatus>(entity.Status),
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
            CommitCount: entity.CommitCount
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
        return new BreakoutRoom(
            Id: entity.Id,
            Name: entity.Name,
            ParentRoomId: entity.ParentRoomId,
            AssignedAgentId: entity.AssignedAgentId,
            Tasks: [],
            Status: Enum.Parse<RoomStatus>(entity.Status),
            RecentMessages: entity.Messages?
                .OrderBy(m => m.SentAt)
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
            )).ToList() ?? [],
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt
        );
    }

    private async Task<List<BreakoutRoom>> GetAllBreakoutRoomsAsync()
    {
        var entities = await _db.BreakoutRooms
            .Include(br => br.Messages)
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

    private async Task TrimMessagesAsync(string roomId)
    {
        // Count committed messages only (pending tracked add is +1)
        var messageCount = await _db.Messages.CountAsync(m => m.RoomId == roomId);
        var totalAfterSave = messageCount + 1; // account for the pending message

        if (totalAfterSave <= MaxRecentMessages) return;

        var toRemove = await _db.Messages
            .Where(m => m.RoomId == roomId)
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
        _db.BreakoutMessages.Add(entity);

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
}
