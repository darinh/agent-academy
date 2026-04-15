using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles all breakout room operations: creation, closure, queries,
/// task linking, agent session history, and breakout reopening.
/// </summary>
public sealed class BreakoutRoomService : IBreakoutRoomService
{
    internal static readonly HashSet<string> TerminalBreakoutStatuses = new(StringComparer.Ordinal)
    {
        nameof(RoomStatus.Completed),
        nameof(RoomStatus.Archived)
    };

    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<BreakoutRoomService> _logger;
    private readonly IAgentCatalog _catalog;
    private readonly IActivityPublisher _activity;
    private readonly IConversationSessionService _sessionService;
    private readonly ITaskQueryService _taskQueries;
    private readonly IAgentLocationService _agentLocations;

    public BreakoutRoomService(
        AgentAcademyDbContext db,
        ILogger<BreakoutRoomService> logger,
        IAgentCatalog catalog,
        IActivityPublisher activity,
        IConversationSessionService sessionService,
        ITaskQueryService taskQueries,
        IAgentLocationService agentLocations)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activity = activity;
        _sessionService = sessionService;
        _taskQueries = taskQueries;
        _agentLocations = agentLocations;
    }

    // ── Creation & Closure ──────────────────────────────────────

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

        await MoveAgentAsync(agentId, parentRoomId, AgentState.Working, breakoutId);

        _activity.Publish(ActivityEventType.RoomCreated, parentRoomId, agentId, null,
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

        await MoveAgentAsync(entity.AssignedAgentId, entity.ParentRoomId, AgentState.Idle);

        entity.Status = nameof(RoomStatus.Archived);
        entity.CloseReason = closeReason.ToString();
        entity.UpdatedAt = DateTime.UtcNow;

        _activity.Publish(ActivityEventType.RoomClosed, entity.ParentRoomId, entity.AssignedAgentId, null,
            $"Breakout room closed: {entity.Name} ({closeReason})");

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Finds the most recent breakout room for a task and reopens it if archived.
    /// Moves the assigned agent back into the breakout to address rejection findings.
    /// </summary>
    public async Task TryReopenBreakoutForTaskAsync(string taskId, string reason, string reviewerName)
    {
        var breakout = await _db.BreakoutRooms
            .Where(b => b.TaskId == taskId && b.Status == nameof(RoomStatus.Archived))
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefaultAsync();

        if (breakout is null) return;

        breakout.Status = nameof(RoomStatus.Active);
        breakout.CloseReason = null;
        breakout.UpdatedAt = DateTime.UtcNow;

        await MoveAgentAsync(breakout.AssignedAgentId, breakout.ParentRoomId,
            AgentState.Working, breakout.Id);

        _activity.Publish(ActivityEventType.RoomStatusChanged, breakout.ParentRoomId,
            breakout.AssignedAgentId, null,
            $"Breakout room reopened for rejected task: {breakout.Name}");

        _db.BreakoutMessages.Add(new BreakoutMessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            BreakoutRoomId = breakout.Id,
            SenderId = "system",
            SenderName = "System",
            SenderRole = "System",
            SenderKind = "System",
            Kind = nameof(MessageKind.System),
            Content = $"⚠️ Task rejected by {reviewerName}:\n{reason}\n\nPlease address the findings and submit for review again.",
            SentAt = DateTime.UtcNow
        });
    }

    // ── Queries ─────────────────────────────────────────────────

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
    /// Returns all active breakout rooms across all parent rooms.
    /// </summary>
    public async Task<List<BreakoutRoom>> GetAllBreakoutRoomsAsync()
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

    // ── Task Linking ────────────────────────────────────────────

    /// <summary>
    /// Links a breakout room to a TaskEntity for reliable lookup during completion.
    /// </summary>
    public async Task SetBreakoutTaskIdAsync(string breakoutRoomId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(breakoutRoomId))
            throw new ArgumentException("Breakout room ID is required.", nameof(breakoutRoomId));
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task ID is required.", nameof(taskId));

        var entity = await _db.BreakoutRooms.FindAsync(breakoutRoomId)
            ?? throw new InvalidOperationException($"Breakout room '{breakoutRoomId}' not found");

        if (string.IsNullOrWhiteSpace(entity.TaskId))
        {
            entity.TaskId = taskId;
            entity.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return;
        }

        if (string.Equals(entity.TaskId, taskId, StringComparison.Ordinal))
            return;

        _logger.LogError(
            "Refusing to relink breakout room {BreakoutRoomId}: existing task {ExistingTaskId}, attempted {AttemptedTaskId}",
            breakoutRoomId,
            entity.TaskId,
            taskId);
        throw new InvalidOperationException(
            $"Breakout room '{breakoutRoomId}' is already linked to task '{entity.TaskId}' and cannot be reassigned to '{taskId}'.");
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

        return await _taskQueries.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.InReview);
    }

    /// <summary>
    /// Ensures a breakout room has a single, explicitly linked TaskEntity.
    /// Task identity is keyed only by the breakout room's persisted TaskId.
    /// </summary>
    public async Task<string> EnsureTaskForBreakoutAsync(
        string breakoutRoomId,
        string title,
        string description,
        string agentId,
        string roomId,
        string? currentPlan = null,
        string? branchName = null)
    {
        if (string.IsNullOrWhiteSpace(breakoutRoomId))
            throw new ArgumentException("Breakout room ID is required.", nameof(breakoutRoomId));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required.", nameof(title));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Description is required.", nameof(description));
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent ID is required.", nameof(agentId));
        if (string.IsNullOrWhiteSpace(roomId))
            throw new ArgumentException("Room ID is required.", nameof(roomId));

        var breakout = await _db.BreakoutRooms.FindAsync(breakoutRoomId)
            ?? throw new InvalidOperationException($"Breakout room '{breakoutRoomId}' not found");

        if (!string.Equals(breakout.ParentRoomId, roomId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Breakout room '{breakoutRoomId}' belongs to room '{breakout.ParentRoomId}', not '{roomId}'.");

        if (!string.Equals(breakout.AssignedAgentId, agentId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Breakout room '{breakoutRoomId}' is assigned to '{breakout.AssignedAgentId}', not '{agentId}'.");

        if (!string.IsNullOrWhiteSpace(breakout.TaskId))
        {
            var existing = await _db.Tasks.FindAsync(breakout.TaskId);
            if (existing is null)
            {
                _logger.LogError(
                    "Breakout room {BreakoutRoomId} references missing task {TaskId}",
                    breakoutRoomId,
                    breakout.TaskId);
                throw new InvalidOperationException(
                    $"Breakout room '{breakoutRoomId}' references missing task '{breakout.TaskId}'.");
            }

            return existing.Id;
        }

        var now = DateTime.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var agent = _catalog.Agents.FirstOrDefault(a => a.Id == agentId);
        var parentWorkspace = await _db.Rooms
            .Where(r => r.Id == roomId)
            .Select(r => r.WorkspacePath)
            .FirstOrDefaultAsync();

        // Inherit priority from parent room's active task (if any)
        var parentPriority = await _db.Tasks
            .Where(t => t.RoomId == roomId && t.Status == nameof(Shared.Models.TaskStatus.Active))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => (int?)t.Priority)
            .FirstOrDefaultAsync() ?? 2; // default Medium

        var entity = new TaskEntity
        {
            Id = taskId,
            Title = title,
            Description = description,
            SuccessCriteria = "",
            Status = nameof(Shared.Models.TaskStatus.Active),
            Type = nameof(TaskType.Feature),
            CurrentPhase = nameof(CollaborationPhase.Implementation),
            CurrentPlan = TaskLifecycleService.ResolveTaskPlanContent(title, currentPlan),
            ValidationStatus = nameof(WorkstreamStatus.NotStarted),
            ValidationSummary = "",
            ImplementationStatus = nameof(WorkstreamStatus.InProgress),
            ImplementationSummary = "",
            PreferredRoles = "[]",
            RoomId = roomId,
            WorkspacePath = parentWorkspace,
            AssignedAgentId = agentId,
            AssignedAgentName = agent?.Name,
            BranchName = branchName,
            Priority = parentPriority,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Tasks.Add(entity);
        breakout.TaskId = taskId;
        breakout.UpdatedAt = now;
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Created TaskEntity {TaskId} for breakout {BreakoutRoomId}: {Title}",
            taskId,
            breakoutRoomId,
            title);
        return taskId;
    }

    // ── Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Returns whether a breakout room status is terminal (Completed or Archived).
    /// Used by callers that need to check breakout status without accessing the static set.
    /// </summary>
    public static bool IsTerminalStatus(string status) => TerminalBreakoutStatuses.Contains(status);

    private BreakoutRoom BuildBreakoutRoomSnapshot(BreakoutRoomEntity entity)
    {
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

    private Task<AgentLocation> MoveAgentAsync(
        string agentId, string roomId, AgentState state, string? breakoutRoomId = null)
        => _agentLocations.MoveAgentAsync(agentId, roomId, state, breakoutRoomId);

    private async Task<string?> GetActiveWorkspacePathAsync()
    {
        return await _db.Workspaces
            .Where(w => w.IsActive)
            .Select(w => w.Path)
            .FirstOrDefaultAsync();
    }
}
