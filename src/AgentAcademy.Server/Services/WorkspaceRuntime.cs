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
    private readonly RoomService _rooms;
    private readonly AgentLocationService _agentLocations;
    private readonly PlanService _plans;
    private readonly CrashRecoveryService _crashRecovery;
    private readonly InitializationService _initialization;

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
        TaskItemService taskItems,
        RoomService rooms,
        AgentLocationService agentLocations,
        PlanService plans,
        CrashRecoveryService crashRecovery,
        InitializationService initialization)
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
        _rooms = rooms;
        _agentLocations = agentLocations;
        _plans = plans;
        _crashRecovery = crashRecovery;
        _initialization = initialization;
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
    public Task InitializeAsync()
        => _initialization.InitializeAsync();

    // ── Server Instance Tracking (delegated to CrashRecoveryService) ──

    /// <summary>
    /// The ID of the current server instance. Set during <see cref="InitializeAsync"/>.
    /// Used by the health endpoint for client reconnect protocol.
    /// </summary>
    public static string? CurrentInstanceId => CrashRecoveryService.CurrentInstanceId;

    /// <summary>
    /// Whether a crash was detected on the most recent startup
    /// (previous instance had no clean shutdown).
    /// </summary>
    public static bool CurrentCrashDetected => CrashRecoveryService.CurrentCrashDetected;

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

    // ── Room Management (delegated to RoomService) ────────────────

    public Task<List<RoomSnapshot>> GetRoomsAsync(bool includeArchived = false)
        => _rooms.GetRoomsAsync(includeArchived);

    public Task<RoomSnapshot?> GetRoomAsync(string roomId)
        => _rooms.GetRoomAsync(roomId);

    public Task<bool> IsMainCollaborationRoomAsync(string roomId)
        => _rooms.IsMainCollaborationRoomAsync(roomId);

    public Task CloseRoomAsync(string roomId)
        => _rooms.CloseRoomAsync(roomId);

    public Task<int> CleanupStaleRoomsAsync()
        => _rooms.CleanupStaleRoomsAsync();

    public Task<RoomSnapshot> CreateRoomAsync(string name, string? description = null)
        => _rooms.CreateRoomAsync(name, description);

    public Task<RoomSnapshot> ReopenRoomAsync(string roomId)
        => _rooms.ReopenRoomAsync(roomId);

    public Task<RoomSnapshot> SetRoomTopicAsync(string roomId, string? topic)
        => _rooms.SetRoomTopicAsync(roomId, topic);

    public Task<(List<ChatEnvelope> Messages, bool HasMore)> GetRoomMessagesAsync(
        string roomId, string? afterMessageId = null, int limit = 50, string? sessionId = null)
        => _rooms.GetRoomMessagesAsync(roomId, afterMessageId, limit, sessionId);

    public Task<RoomSnapshot?> RenameRoomAsync(string roomId, string newName)
        => _rooms.RenameRoomAsync(roomId, newName);

    public Task<string?> GetProjectNameForRoomAsync(string roomId)
        => _rooms.GetProjectNameForRoomAsync(roomId);

    public Task<string?> GetActiveProjectNameAsync()
        => _rooms.GetActiveProjectNameAsync();

    public async Task<RoomSnapshot> CreateDefaultRoomAsync()
    {
        var existing = await _db.Rooms.FindAsync(_catalog.DefaultRoomId);
        if (existing is not null)
        {
            return await _rooms.BuildRoomSnapshotAsync(existing);
        }

        await InitializeAsync();
        var room = await _db.Rooms.FindAsync(_catalog.DefaultRoomId);
        return await _rooms.BuildRoomSnapshotAsync(room!);
    }

    public Task<string> EnsureDefaultRoomForWorkspaceAsync(string workspacePath)
        => _rooms.EnsureDefaultRoomForWorkspaceAsync(workspacePath);

    public Task<List<string>> GetRoomsWithPendingHumanMessagesAsync()
        => _rooms.GetRoomsWithPendingHumanMessagesAsync();


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

        var roomSnapshot = await _rooms.BuildRoomSnapshotAsync(roomEntity);

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
            await _rooms.TryAutoArchiveRoomAsync(roomId);
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

    // ── Phase Management (delegated to RoomService) ───────────────

    public Task<RoomSnapshot> TransitionPhaseAsync(
        string roomId, CollaborationPhase targetPhase, string? reason = null)
        => _rooms.TransitionPhaseAsync(roomId, targetPhase, reason);

    // ── Agent Location ──────────────────────────────────────────

    /// <summary>
    /// Returns all agent locations.
    /// </summary>
    public Task<List<AgentLocation>> GetAgentLocationsAsync()
        => _agentLocations.GetAgentLocationsAsync();

    /// <summary>
    /// Returns a single agent's location, or null if not tracked.
    /// </summary>
    public Task<AgentLocation?> GetAgentLocationAsync(string agentId)
        => _agentLocations.GetAgentLocationAsync(agentId);

    /// <summary>
    /// Moves an agent to a new room/state.
    /// </summary>
    public Task<AgentLocation> MoveAgentAsync(
        string agentId, string roomId, AgentState state, string? breakoutRoomId = null)
        => _agentLocations.MoveAgentAsync(agentId, roomId, state, breakoutRoomId);

    // ── Breakout Rooms ──────────────────────────────────────────

    public Task<BreakoutRoom> CreateBreakoutRoomAsync(
        string parentRoomId, string agentId, string name)
        => _breakouts.CreateBreakoutRoomAsync(parentRoomId, agentId, name);

    public Task CloseBreakoutRoomAsync(
        string breakoutId,
        BreakoutRoomCloseReason closeReason = BreakoutRoomCloseReason.Completed)
        => _breakouts.CloseBreakoutRoomAsync(breakoutId, closeReason);

    public Task<CrashRecoveryService.CrashRecoveryResult> RecoverFromCrashAsync(string mainRoomId)
        => _crashRecovery.RecoverFromCrashAsync(mainRoomId);



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
    public Task<PlanContent?> GetPlanAsync(string roomId)
        => _plans.GetPlanAsync(roomId);

    /// <summary>
    /// Creates or updates the plan for a room.
    /// </summary>
    public Task SetPlanAsync(string roomId, string content)
        => _plans.SetPlanAsync(roomId, content);

    /// <summary>
    /// Deletes the plan for a room. Returns true if a plan was deleted.
    /// </summary>
    public Task<bool> DeletePlanAsync(string roomId)
        => _plans.DeletePlanAsync(roomId);

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

    private Task<List<BreakoutRoom>> GetAllBreakoutRoomsAsync()
        => _breakouts.GetAllBreakoutRoomsAsync();

    public Task<List<BreakoutRoom>> GetAgentSessionsAsync(string agentId)
        => _breakouts.GetAgentSessionsAsync(agentId);

    private static string Normalize(string value)
        => RoomService.Normalize(value);

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
