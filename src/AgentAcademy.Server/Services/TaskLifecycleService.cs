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
public sealed class TaskLifecycleService
{
    private const int MaxReviewRounds = 5;

    /// <summary>
    /// Valid evidence phases.
    /// </summary>
    public static readonly HashSet<string> ValidEvidencePhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "Baseline", "After", "Review"
    };

    /// <summary>
    /// Valid spec-task link types.
    /// </summary>
    public static readonly HashSet<string> ValidLinkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Implements", "Modifies", "Fixes", "References"
    };

    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<TaskLifecycleService> _logger;
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityPublisher _activity;

    public TaskLifecycleService(
        AgentAcademyDbContext db,
        ILogger<TaskLifecycleService> logger,
        AgentCatalogOptions catalog,
        ActivityPublisher activity)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activity = activity;
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
        return TaskQueryService.BuildTaskSnapshot(entity);
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

        if (entity.ReviewRounds >= MaxReviewRounds)
            throw new InvalidOperationException(
                $"Task '{taskId}' has reached the maximum of {MaxReviewRounds} review rounds. Consider cancelling the task or breaking it into smaller pieces.");

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

    // ── Task Evidence Ledger ──────────────────────────────────

    /// <summary>
    /// Records a structured verification check against a task.
    /// </summary>
    public async Task<TaskEvidence> RecordEvidenceAsync(
        string taskId, string agentId, string agentName,
        EvidencePhase phase, string checkName, string tool,
        string? command, int? exitCode, string? outputSnippet, bool passed)
    {
        var task = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var entity = new TaskEvidenceEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            Phase = phase.ToString(),
            CheckName = checkName,
            Tool = tool,
            Command = command,
            ExitCode = exitCode,
            OutputSnippet = outputSnippet?.Length > 500 ? outputSnippet[..500] : outputSnippet,
            Passed = passed,
            AgentId = agentId,
            AgentName = agentName,
            CreatedAt = DateTime.UtcNow
        };
        _db.TaskEvidence.Add(entity);

        Publish(ActivityEventType.EvidenceRecorded, task.RoomId, agentId, taskId,
            $"{agentName} recorded {phase.ToString().ToLower()} evidence: {checkName} — {(passed ? "passed" : "FAILED")}");

        await _db.SaveChangesAsync();
        return TaskQueryService.BuildTaskEvidence(entity);
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
        var task = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var allEvidence = await _db.TaskEvidence
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();

        var evidenceModels = allEvidence.Select(TaskQueryService.BuildTaskEvidence).ToList();
        var currentStatus = task.Status;
        string targetStatus;
        int requiredChecks;
        string requiredPhaseFilter;
        List<string> suggestedChecks;

        switch (currentStatus)
        {
            case "Active":
                targetStatus = "AwaitingValidation";
                requiredChecks = 1;
                requiredPhaseFilter = "After";
                suggestedChecks = new List<string> { "build", "tests", "type-check" };
                break;
            case "AwaitingValidation":
                targetStatus = "InReview";
                requiredChecks = 2;
                requiredPhaseFilter = "After";
                suggestedChecks = new List<string> { "build", "tests", "type-check", "lint" };
                break;
            case "InReview":
                targetStatus = "Approved";
                requiredChecks = 1;
                requiredPhaseFilter = "Review";
                suggestedChecks = new List<string> { "code-review" };
                break;
            default:
                targetStatus = "N/A";
                requiredChecks = 0;
                requiredPhaseFilter = "After";
                suggestedChecks = new List<string>();
                break;
        }

        var relevantPassed = allEvidence
            .Where(e => e.Phase == requiredPhaseFilter && e.Passed)
            .Select(e => e.CheckName)
            .Distinct()
            .ToList();

        var missingChecks = suggestedChecks
            .Where(s => !relevantPassed.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Publish(ActivityEventType.GateChecked, task.RoomId, null, taskId,
            $"Gate check for {currentStatus} → {targetStatus}: {relevantPassed.Count}/{requiredChecks} checks passed" +
            (missingChecks.Count > 0 ? $". Missing: {string.Join(", ", missingChecks)}" : ""));

        return new GateCheckResult(
            TaskId: taskId,
            CurrentPhase: currentStatus,
            TargetPhase: targetStatus,
            Met: relevantPassed.Count >= requiredChecks,
            RequiredChecks: requiredChecks,
            PassedChecks: relevantPassed.Count,
            MissingChecks: missingChecks,
            Evidence: evidenceModels
        );
    }

    // ── Spec–Task Linking ───────────────────────────────────────

    /// <summary>
    /// Links a task to a spec section. Idempotent — updates link type if the pair already exists.
    /// </summary>
    public async Task<SpecTaskLink> LinkTaskToSpecAsync(
        string taskId, string specSectionId, string agentId, string agentName,
        string linkType = "Implements", string? note = null)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("taskId is required");
        if (string.IsNullOrWhiteSpace(specSectionId))
            throw new ArgumentException("specSectionId is required");
        if (!ValidLinkTypes.Contains(linkType))
            throw new ArgumentException(
                $"Invalid link type '{linkType}'. Valid types: {string.Join(", ", ValidLinkTypes)}");

        var task = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        // Upsert with retry: catch unique constraint violation on concurrent insert
        try
        {
            return await UpsertSpecLinkCoreAsync(
                task, taskId, specSectionId, agentId, agentName, linkType, note);
        }
        catch (DbUpdateException)
        {
            // Concurrent insert hit unique constraint — reload and update
            _db.ChangeTracker.Clear();
            return await UpsertSpecLinkCoreAsync(
                task, taskId, specSectionId, agentId, agentName, linkType, note);
        }
    }

    private async Task<SpecTaskLink> UpsertSpecLinkCoreAsync(
        TaskEntity task, string taskId, string specSectionId,
        string agentId, string agentName, string linkType, string? note)
    {
        var existing = await _db.SpecTaskLinks
            .FirstOrDefaultAsync(l => l.TaskId == taskId && l.SpecSectionId == specSectionId);

        if (existing is not null)
        {
            existing.LinkType = linkType;
            existing.Note = note ?? existing.Note;
            existing.LinkedByAgentId = agentId;
            existing.LinkedByAgentName = agentName;

            Publish(ActivityEventType.SpecTaskLinked, task.RoomId, agentId, taskId,
                $"{agentName} updated spec link: {specSectionId} → {task.Title}");
            await _db.SaveChangesAsync();

            return TaskQueryService.BuildSpecTaskLink(existing);
        }

        var entity = new SpecTaskLinkEntity
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            TaskId = taskId,
            SpecSectionId = specSectionId,
            LinkType = linkType,
            LinkedByAgentId = agentId,
            LinkedByAgentName = agentName,
            Note = note,
            CreatedAt = DateTime.UtcNow
        };

        _db.SpecTaskLinks.Add(entity);
        Publish(ActivityEventType.SpecTaskLinked, task.RoomId, agentId, taskId,
            $"{agentName} linked spec {specSectionId} to task: {Truncate(task.Title, 60)}");
        await _db.SaveChangesAsync();

        return TaskQueryService.BuildSpecTaskLink(entity);
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

        await _db.SaveChangesAsync();
        return (TaskQueryService.BuildTaskSnapshot(entity), entity.RoomId);
    }

    /// <summary>
    /// Rejects a task (from Approved or Completed state). Updates status, adds review message,
    /// publishes activity. Does NOT save — the caller owns the unit of work so room/breakout
    /// reopen can be committed atomically with the rejection.
    /// </summary>
    public async Task<RejectTaskResult> RejectTaskCoreAsync(
        string taskId, string reviewerAgentId, string reason, string? revertCommitSha = null)
    {
        var entity = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found");

        var currentStatus = entity.Status;
        if (currentStatus != nameof(Shared.Models.TaskStatus.Approved) &&
            currentStatus != nameof(Shared.Models.TaskStatus.Completed))
            throw new InvalidOperationException(
                $"Task '{taskId}' is in '{currentStatus}' state — must be Approved or Completed to reject");

        if (entity.ReviewRounds >= MaxReviewRounds)
            throw new InvalidOperationException(
                $"Task '{taskId}' has reached the maximum of {MaxReviewRounds} review rounds. Consider cancelling the task or breaking it into smaller pieces.");

        var now = DateTime.UtcNow;
        var wasCompleted = currentStatus == nameof(Shared.Models.TaskStatus.Completed);

        entity.Status = nameof(Shared.Models.TaskStatus.ChangesRequested);
        entity.ReviewerAgentId = reviewerAgentId;
        entity.ReviewRounds++;
        entity.UpdatedAt = now;

        if (wasCompleted)
        {
            entity.MergeCommitSha = null;
            entity.CompletedAt = null;
        }

        var reviewerName = _catalog.Agents.FirstOrDefault(a => a.Id == reviewerAgentId)?.Name ?? reviewerAgentId;

        var statusNote = revertCommitSha is not null ? " (merge reverted)" : "";
        if (!string.IsNullOrEmpty(entity.RoomId))
        {
            var msgEntity = CreateMessageEntity(entity.RoomId, MessageKind.Review,
                $"❌ **Rejected** by {reviewerName}{statusNote}\n\n{reason}", null, now);
            _db.Messages.Add(msgEntity);
        }

        Publish(ActivityEventType.TaskRejected, entity.RoomId, reviewerAgentId, taskId,
            $"{reviewerName} rejected task: {Truncate(entity.Title, 80)}");

        // NOTE: Does NOT call SaveChangesAsync — the caller (TaskOrchestrationService.RejectTaskAsync)
        // performs room/breakout reopen and saves everything in one atomic commit.

        return new RejectTaskResult(
            Snapshot: TaskQueryService.BuildTaskSnapshot(entity),
            RoomId: entity.RoomId,
            TaskId: taskId,
            ReviewerName: reviewerName);
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

    /// <summary>
    /// Result of a task rejection, containing the info needed for room/breakout reopen.
    /// </summary>
    public sealed record RejectTaskResult(
        TaskSnapshot Snapshot,
        string? RoomId,
        string TaskId,
        string ReviewerName);

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
