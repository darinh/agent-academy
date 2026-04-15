using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Coordinates task operations that cross room, agent, and task boundaries.
/// </summary>
public sealed class TaskOrchestrationService : ITaskOrchestrationService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<TaskOrchestrationService> _logger;
    private readonly IAgentCatalog _catalog;
    private readonly ActivityPublisher _activity;
    private readonly TaskLifecycleService _taskLifecycle;
    private readonly TaskQueryService _taskQueries;
    private readonly RoomService _rooms;
    private readonly RoomSnapshotBuilder _snapshots;
    private readonly RoomLifecycleService _roomLifecycle;
    private readonly AgentLocationService _agentLocations;
    private readonly MessageService _messages;
    private readonly BreakoutRoomService _breakouts;

    private const int MaxBulkSize = 50;

    public TaskOrchestrationService(
        AgentAcademyDbContext db,
        ILogger<TaskOrchestrationService> logger,
        IAgentCatalog catalog,
        ActivityPublisher activity,
        TaskLifecycleService taskLifecycle,
        TaskQueryService taskQueries,
        RoomService rooms,
        RoomSnapshotBuilder snapshots,
        RoomLifecycleService roomLifecycle,
        AgentLocationService agentLocations,
        MessageService messages,
        BreakoutRoomService breakouts)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activity = activity;
        _taskLifecycle = taskLifecycle;
        _taskQueries = taskQueries;
        _rooms = rooms;
        _snapshots = snapshots;
        _roomLifecycle = roomLifecycle;
        _agentLocations = agentLocations;
        _messages = messages;
        _breakouts = breakouts;
    }

    /// <summary>
    /// Creates a new task, optionally in an existing room or a new room.
    /// Handles room creation/lookup, task entity staging, sprint association,
    /// agent auto-join, and snapshot building.
    /// </summary>
    public async Task<TaskAssignmentResult> CreateTaskAsync(TaskAssignmentRequest request)
    {
        var now = DateTime.UtcNow;
        var correlationId = request.CorrelationId ?? Guid.NewGuid().ToString("N");

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
            var roomId = $"{RoomService.Normalize(request.Title)}-{Guid.NewGuid().ToString("N")[..8]}";
            var activeWorkspace = await _db.Workspaces
                .Where(w => w.IsActive)
                .Select(w => w.Path)
                .FirstOrDefaultAsync();

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

        var (task, activity) = _taskLifecycle.StageNewTask(
            request, roomEntity.Id, roomEntity.WorkspacePath, isNewRoom, correlationId);

        await _taskLifecycle.AssociateTaskWithActiveSprintAsync(task.Id, roomEntity.WorkspacePath);

        await _db.SaveChangesAsync();

        if (isNewRoom)
        {
            foreach (var agent in _catalog.Agents.Where(a => a.AutoJoinDefaultRoom))
            {
                try
                {
                    var loc = await _db.AgentLocations.FindAsync(agent.Id);
                    if (loc is not null && loc.State == nameof(AgentState.Working))
                        continue;

                    await _agentLocations.MoveAgentAsync(agent.Id, roomEntity.Id, AgentState.Idle);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to auto-join agent {AgentId} into room {RoomId}; skipping",
                        agent.Id, roomEntity.Id);
                }
            }
        }

        var roomSnapshot = await _snapshots.BuildRoomSnapshotAsync(roomEntity);

        return new TaskAssignmentResult(
            CorrelationId: correlationId,
            Room: roomSnapshot,
            Task: task,
            Activity: activity
        );
    }

    /// <summary>
    /// Completes a task and auto-archives its room if all tasks in it are terminal.
    /// </summary>
    public async Task<TaskSnapshot> CompleteTaskAsync(
        string taskId, int commitCount, List<string>? testsCreated = null, string? mergeCommitSha = null)
    {
        var (snapshot, roomId) = await _taskLifecycle.CompleteTaskCoreAsync(
            taskId, commitCount, testsCreated, mergeCommitSha);

        if (!string.IsNullOrEmpty(roomId))
        {
            await _roomLifecycle.TryAutoArchiveRoomAsync(roomId);
        }

        return snapshot;
    }

    /// <summary>
    /// Rejects a task, reopening its room and breakout room so the
    /// assigned agent can address the rejection findings.
    /// </summary>
    public async Task<TaskSnapshot> RejectTaskAsync(
        string taskId, string reviewerAgentId, string reason, string? revertCommitSha = null)
    {
        var result = await _taskLifecycle.RejectTaskCoreAsync(
            taskId, reviewerAgentId, reason, revertCommitSha);

        if (!string.IsNullOrEmpty(result.RoomId))
        {
            await TryReopenRoomForTaskAsync(result.RoomId);
        }

        await _breakouts.TryReopenBreakoutForTaskAsync(result.TaskId, reason, result.ReviewerName);

        await _db.SaveChangesAsync();
        return result.Snapshot;
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

        await _messages.PostSystemStatusAsync(entity.RoomId, message);
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

        _activity.Publish(ActivityEventType.RoomStatusChanged, roomId, null, null,
            $"Room reopened (task rejected): {room.Name}");

        _logger.LogInformation(
            "Reopened auto-archived room '{RoomId}' ({RoomName}) due to task rejection",
            roomId, room.Name);
    }

    // ── Bulk Operations ─────────────────────────────────────────

    /// <summary>
    /// Updates the status of multiple tasks and publishes activity events.
    /// Throws <see cref="ArgumentException"/> if <paramref name="taskIds"/> exceeds
    /// <see cref="MaxBulkSize"/> or the status is disallowed.
    /// </summary>
    public async Task<BulkOperationResult> BulkUpdateStatusAsync(
        IReadOnlyList<string> taskIds, Shared.Models.TaskStatus status)
    {
        if (taskIds.Count > MaxBulkSize)
            throw new ArgumentException($"Maximum {MaxBulkSize} tasks per bulk operation.");

        var result = await _taskQueries.BulkUpdateStatusAsync(taskIds, status);

        foreach (var task in result.Updated)
        {
            _activity.Publish(
                ActivityEventType.TaskStatusUpdated, null, null, task.Id,
                $"Task '{task.Title}' status → {task.Status} (bulk)");
        }

        if (result.Updated.Count > 0)
            await _db.SaveChangesAsync();

        return result;
    }

    /// <summary>
    /// Assigns multiple tasks to a single agent and publishes activity events.
    /// Throws <see cref="ArgumentException"/> if <paramref name="taskIds"/> exceeds
    /// <see cref="MaxBulkSize"/> or the agent can't be resolved.
    /// </summary>
    public async Task<BulkOperationResult> BulkAssignAsync(
        IReadOnlyList<string> taskIds, string agentId, string? agentName)
    {
        if (taskIds.Count > MaxBulkSize)
            throw new ArgumentException($"Maximum {MaxBulkSize} tasks per bulk operation.");

        var result = await _taskQueries.BulkAssignAsync(taskIds, agentId, agentName);

        foreach (var task in result.Updated)
        {
            _activity.Publish(
                ActivityEventType.TaskStatusUpdated, null, null, task.Id,
                $"Task '{task.Title}' assigned to {task.AssignedAgentName} (bulk)");
        }

        if (result.Updated.Count > 0)
            await _db.SaveChangesAsync();

        return result;
    }
}
