using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles task lifecycle transitions that have side-effects (activity events, review messages).
/// Covers claim/release, review workflow, evidence, gates, and spec linking.
/// Task entity mutations for create/complete/reject are orchestrated by
/// TaskOrchestrationService which delegates here for the task-state changes.
/// </summary>
public sealed partial class TaskLifecycleService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<TaskLifecycleService> _logger;
    private readonly IAgentCatalog _catalog;
    private readonly ActivityPublisher _activity;
    private readonly TaskDependencyService _dependencies;

    public TaskLifecycleService(
        AgentAcademyDbContext db,
        ILogger<TaskLifecycleService> logger,
        IAgentCatalog catalog,
        ActivityPublisher activity,
        TaskDependencyService dependencies)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activity = activity;
        _dependencies = dependencies;
    }

    // ── Task State Transitions ──────────────────────────────────

    /// <summary>
    /// Claims a task for an agent. Prevents double-claiming by another agent.
    /// Auto-activates tasks in Queued status.
    /// </summary>
    public async Task<TaskSnapshot> ClaimTaskAsync(string taskId, string agentId, string agentName)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        // Block claiming tasks with unmet dependencies
        var blockers = await _dependencies.GetBlockingTasksAsync(taskId);
        if (blockers.Count > 0)
        {
            var blockerList = string.Join(", ", blockers.Select(b => $"'{b.Title}' ({b.Status})"));
            throw new InvalidOperationException(
                $"Task '{taskId}' has unmet dependencies: {blockerList}. Complete them first.");
        }

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
        return TaskQueryService.BuildTaskSnapshot(entity);
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
        return TaskQueryService.BuildTaskSnapshot(entity);
    }

    /// <summary>
    /// Syncs PR status on a task. Returns null if no change occurred.
    /// </summary>
    public async Task<TaskSnapshot?> SyncTaskPrStatusAsync(
        string taskId, PullRequestStatus newStatus)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var currentStatus = entity.PullRequestStatus;
        if (string.Equals(currentStatus, newStatus.ToString(), StringComparison.Ordinal))
            return null; // no change

        var oldStatus = currentStatus ?? "None";
        entity.PullRequestStatus = newStatus.ToString();
        entity.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.TaskPrStatusChanged, entity.RoomId, null, taskId,
            $"PR #{entity.PullRequestNumber} status changed: {oldStatus} → {newStatus}");

        await _db.SaveChangesAsync();
        return TaskQueryService.BuildTaskSnapshot(entity);
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

        return TaskQueryService.BuildTaskComment(comment);
    }

    // ── Task Create / Complete / Reject ────────────────────────

    /// <summary>
    /// Stages a new task entity, messages, and activity events against the supplied room.
    /// Does NOT call SaveChangesAsync — the caller owns the unit of work so it can
    /// perform additional operations (agent auto-join, room snapshot) before committing.
    /// </summary>
    /// <returns>The staged TaskSnapshot and the initial activity event.</returns>
    public (TaskSnapshot Task, ActivityEvent Activity) StageNewTask(
        TaskAssignmentRequest request, string roomId, string? workspacePath,
        bool isNewRoom, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("Title is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Description))
            throw new ArgumentException("Description is required", nameof(request));

        var now = DateTime.UtcNow;
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
            RoomId = roomId,
            WorkspacePath = workspacePath,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Tasks.Add(taskEntity);

        var assignmentMsg = CreateMessageEntity(
            roomId, MessageKind.TaskAssignment,
            $"New task assigned: {request.Title}\n\n{request.Description}",
            correlationId, now);
        _db.Messages.Add(assignmentMsg);

        var planMsg = CreateMessageEntity(
            roomId, MessageKind.Coordination,
            $"Phase set to Planning. Begin by reviewing requirements and proposing an approach.",
            correlationId, now);
        _db.Messages.Add(planMsg);

        if (isNewRoom)
        {
            Publish(ActivityEventType.RoomCreated, roomId, null, taskId,
                $"Room created for task: {request.Title}");
        }

        var activity = Publish(ActivityEventType.TaskCreated, roomId, null, taskId,
            $"Task created: {request.Title}", correlationId);

        Publish(ActivityEventType.PhaseChanged, roomId, null, taskId,
            "Phase changed to Planning");

        return (task with { WorkspacePath = workspacePath }, activity);
    }

    /// <summary>
    /// Associates a staged task with the active sprint for its workspace, if one exists.
    /// Must be called after StageNewTask and before SaveChangesAsync.
    /// </summary>
    public async Task AssociateTaskWithActiveSprintAsync(string taskId, string? workspacePath)
    {
        if (workspacePath is null) return;

        var activeSprint = await _db.Sprints
            .FirstOrDefaultAsync(s => s.WorkspacePath == workspacePath && s.Status == "Active");

        if (activeSprint is not null)
        {
            var taskEntity = _db.Tasks.Local.FirstOrDefault(t => t.Id == taskId)
                ?? await _db.Tasks.FindAsync(taskId);
            if (taskEntity is not null)
                taskEntity.SprintId = activeSprint.Id;
        }
    }

    /// <summary>
    /// Marks a task as completed. Updates status, timestamps, and commit metadata.
    /// Saves changes. Returns the snapshot and the room ID for post-completion room cleanup.
    /// </summary>
    public async Task<(TaskSnapshot Snapshot, string? RoomId)> CompleteTaskCoreAsync(
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

        // Before saving, compute which downstream tasks become unblocked.
        // The query treats this task as already satisfied (not yet persisted).
        var unblockedTasks = await _dependencies.GetTasksUnblockedByCompletionAsync(taskId);
        foreach (var (unblockedTaskId, title, unblockedRoomId) in unblockedTasks)
        {
            Publish(ActivityEventType.TaskUnblocked, unblockedRoomId, null, unblockedTaskId,
                $"Task unblocked: \"{Truncate(title, 80)}\" — all dependencies now satisfied");
        }

        await _db.SaveChangesAsync();
        return (TaskQueryService.BuildTaskSnapshot(entity), entity.RoomId);
    }

    /// <summary>
    /// Generates default plan content for a task when no explicit plan is provided.
    /// </summary>
    internal static string ResolveTaskPlanContent(string title, string? currentPlan)
    {
        if (!string.IsNullOrWhiteSpace(currentPlan))
            return currentPlan.Trim();

        return $"# {title}\n\n## Plan\n1. Review requirements\n2. Design solution\n3. Implement\n4. Validate";
    }

    // ── Shared Helpers ──────────────────────────────────────────

    private ActivityEvent Publish(
        ActivityEventType type,
        string? roomId,
        string? actorId,
        string? taskId,
        string message,
        string? correlationId = null,
        ActivitySeverity severity = ActivitySeverity.Info)
        => _activity.Publish(type, roomId, actorId, taskId, message, correlationId, severity);

    private static MessageEntity CreateMessageEntity(
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

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
