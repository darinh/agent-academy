using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Delegation facade for the Agent Academy workspace.
/// Routes calls to focused sub-services; contains no business logic itself.
/// Ported from v1 TypeScript WorkspaceRuntime; orchestration logic now
/// lives in TaskOrchestrationService and other leaf services.
/// </summary>
public sealed class WorkspaceRuntime
{
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityPublisher _activity;
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
    private readonly TaskOrchestrationService _taskOrchestration;

    public WorkspaceRuntime(
        AgentCatalogOptions catalog,
        ActivityPublisher activity,
        TaskQueryService taskQueries,
        TaskLifecycleService taskLifecycle,
        MessageService messages,
        BreakoutRoomService breakouts,
        TaskItemService taskItems,
        RoomService rooms,
        AgentLocationService agentLocations,
        PlanService plans,
        CrashRecoveryService crashRecovery,
        InitializationService initialization,
        TaskOrchestrationService taskOrchestration)
    {
        _catalog = catalog;
        _activity = activity;
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
        _taskOrchestration = taskOrchestration;
    }

    // ── Initialization ──────────────────────────────────────────

    /// <summary>
    /// Returns the catalog's default room ID (e.g. "main").
    /// </summary>
    public string DefaultRoomId => _catalog.DefaultRoomId;

    /// <summary>
    /// Returns the path of the currently active workspace, or null if none.
    /// </summary>
    public Task<string?> GetActiveWorkspacePathAsync()
        => _rooms.GetActiveWorkspacePathAsync();

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

    public Task<string> EnsureDefaultRoomForWorkspaceAsync(string workspacePath)
        => _rooms.EnsureDefaultRoomForWorkspaceAsync(workspacePath);

    public Task<List<string>> GetRoomsWithPendingHumanMessagesAsync()
        => _rooms.GetRoomsWithPendingHumanMessagesAsync();


    // ── Task Management ─────────────────────────────────────────

    /// <summary>
    /// Creates a new task, optionally in an existing room or a new room.
    /// </summary>
    public Task<TaskAssignmentResult> CreateTaskAsync(TaskAssignmentRequest request)
        => _taskOrchestration.CreateTaskAsync(request);

    /// <summary>
    /// Returns all tasks.
    /// </summary>
    public Task<List<TaskSnapshot>> GetTasksAsync(string? sprintId = null)
        => _taskQueries.GetTasksAsync(sprintId);

    /// <summary>
    /// Returns a specific task by ID, or null if not found.
    /// </summary>
    public Task<TaskSnapshot?> GetTaskAsync(string taskId)
        => _taskQueries.GetTaskAsync(taskId);

    /// <summary>
    /// Finds a task by title. Returns the first non-cancelled match, or null.
    /// </summary>
    public Task<TaskSnapshot?> FindTaskByTitleAsync(string title)
        => _taskQueries.FindTaskByTitleAsync(title);

    /// <summary>
    /// Assigns an agent to a task. Validates the agent exists in the catalog.
    /// </summary>
    public Task<TaskSnapshot> AssignTaskAsync(string taskId, string agentId, string agentName)
        => _taskQueries.AssignTaskAsync(taskId, agentId, agentName);

    /// <summary>
    /// Updates a task's status. Automatically sets StartedAt/CompletedAt as appropriate.
    /// </summary>
    public Task<TaskSnapshot> UpdateTaskStatusAsync(string taskId, Shared.Models.TaskStatus status)
        => _taskQueries.UpdateTaskStatusAsync(taskId, status);

    /// <summary>
    /// Records a branch name on a task. Branch metadata is write-once per task.
    /// </summary>
    public Task<TaskSnapshot> UpdateTaskBranchAsync(string taskId, string branchName)
        => _taskQueries.UpdateTaskBranchAsync(taskId, branchName);

    /// <summary>
    /// Records PR information on a task.
    /// </summary>
    public Task<TaskSnapshot> UpdateTaskPrAsync(
        string taskId, string url, int number, Shared.Models.PullRequestStatus status)
        => _taskQueries.UpdateTaskPrAsync(taskId, url, number, status);

    /// <summary>
    /// Updates only the PR status on a task. Used by PR sync polling.
    /// Emits a TaskPrStatusChanged activity event when the status actually changes.
    /// Returns null if the status was already up-to-date.
    /// </summary>
    public Task<TaskSnapshot?> SyncTaskPrStatusAsync(
        string taskId, Shared.Models.PullRequestStatus newStatus)
        => _taskLifecycle.SyncTaskPrStatusAsync(taskId, newStatus);

    /// <summary>
    /// Returns task IDs that have open (non-terminal) pull requests for polling.
    /// </summary>
    public Task<List<(string TaskId, int PrNumber)>> GetTasksWithActivePrsAsync()
        => _taskQueries.GetTasksWithActivePrsAsync();
    public Task<TaskSnapshot> CompleteTaskAsync(
        string taskId, int commitCount, List<string>? testsCreated = null, string? mergeCommitSha = null)
        => _taskOrchestration.CompleteTaskAsync(taskId, commitCount, testsCreated, mergeCommitSha);

    // ── Task State Commands ──────────────────────────────────────

    /// <summary>
    /// Claims a task for an agent. Prevents double-claiming by another agent.
    /// Auto-activates tasks in Queued status.
    /// </summary>
    public Task<TaskSnapshot> ClaimTaskAsync(string taskId, string agentId, string agentName)
        => _taskLifecycle.ClaimTaskAsync(taskId, agentId, agentName);

    /// <summary>
    /// Releases a task claim. Only the currently assigned agent can release.
    /// </summary>
    public Task<TaskSnapshot> ReleaseTaskAsync(string taskId, string agentId)
        => _taskLifecycle.ReleaseTaskAsync(taskId, agentId);

    /// <summary>
    /// Approves a task after review. Records the reviewer and increments review rounds.
    /// </summary>
    public Task<TaskSnapshot> ApproveTaskAsync(string taskId, string reviewerAgentId, string? findings = null)
        => _taskLifecycle.ApproveTaskAsync(taskId, reviewerAgentId, findings);

    /// <summary>
    /// Requests changes on a task after review. Records the reviewer and increments review rounds.
    /// </summary>
    public Task<TaskSnapshot> RequestChangesAsync(string taskId, string reviewerAgentId, string findings)
        => _taskLifecycle.RequestChangesAsync(taskId, reviewerAgentId, findings);

    /// <summary>
    /// Rejects an approved or completed task, returning it to ChangesRequested.
    /// For completed tasks, clears the merge metadata. Reopens the breakout room
    /// so the assigned agent can address the rejection findings.
    /// </summary>
    public Task<TaskSnapshot> RejectTaskAsync(
        string taskId, string reviewerAgentId, string reason, string? revertCommitSha = null)
        => _taskOrchestration.RejectTaskAsync(taskId, reviewerAgentId, reason, revertCommitSha);

    /// <summary>
    /// Returns tasks that are pending review (InReview or AwaitingValidation).
    /// </summary>
    public Task<List<TaskSnapshot>> GetReviewQueueAsync()
        => _taskQueries.GetReviewQueueAsync();

    /// <summary>
    /// Posts a system note to the room associated with a task.
    /// No-op if the task has no room.
    /// </summary>
    public Task PostTaskNoteAsync(string taskId, string message)
        => _taskOrchestration.PostTaskNoteAsync(taskId, message);

    // ── Task Comments ──────────────────────────────────────────

    /// <summary>
    /// Adds a comment or finding to a task.
    /// </summary>
    public Task<TaskComment> AddTaskCommentAsync(
        string taskId, string agentId, string agentName,
        TaskCommentType commentType, string content)
        => _taskLifecycle.AddTaskCommentAsync(taskId, agentId, agentName, commentType, content);

    /// <summary>
    /// Gets all comments for a task, ordered by creation time.
    /// </summary>
    public Task<List<TaskComment>> GetTaskCommentsAsync(string taskId)
        => _taskQueries.GetTaskCommentsAsync(taskId);

    /// <summary>
    /// Gets the count of comments for a task.
    /// </summary>
    public Task<int> GetTaskCommentCountAsync(string taskId)
        => _taskQueries.GetTaskCommentCountAsync(taskId);

    // ── Task Evidence Ledger ──────────────────────────────────

    /// <summary>
    /// Valid evidence phases.
    /// </summary>
    public static readonly HashSet<string> ValidEvidencePhases = TaskLifecycleService.ValidEvidencePhases;

    /// <summary>
    /// Records a structured verification check against a task.
    /// </summary>
    public Task<TaskEvidence> RecordEvidenceAsync(
        string taskId, string agentId, string agentName,
        EvidencePhase phase, string checkName, string tool,
        string? command, int? exitCode, string? outputSnippet, bool passed)
        => _taskLifecycle.RecordEvidenceAsync(
            taskId, agentId, agentName, phase, checkName, tool,
            command, exitCode, outputSnippet, passed);

    /// <summary>
    /// Gets all evidence for a task, optionally filtered by phase.
    /// </summary>
    public Task<List<TaskEvidence>> GetTaskEvidenceAsync(string taskId, EvidencePhase? phase = null)
        => _taskQueries.GetTaskEvidenceAsync(taskId, phase);

    /// <summary>
    /// Checks whether a task meets the minimum evidence requirements for a phase transition.
    /// Gate definitions (based on task status):
    /// - Active → AwaitingValidation: ≥1 "After" check passed
    /// - AwaitingValidation → InReview: ≥2 "After" checks passed
    /// - InReview → Approved: ≥1 "Review" check passed
    /// </summary>
    public Task<GateCheckResult> CheckGatesAsync(string taskId)
        => _taskLifecycle.CheckGatesAsync(taskId);

    // ── Spec–Task Linking ───────────────────────────────────────

    /// <summary>
    /// Valid spec-task link types.
    /// </summary>
    public static readonly HashSet<string> ValidLinkTypes = TaskLifecycleService.ValidLinkTypes;

    /// <summary>
    /// Links a task to a spec section. Idempotent — updates link type if the pair already exists.
    /// </summary>
    public Task<SpecTaskLink> LinkTaskToSpecAsync(
        string taskId, string specSectionId, string agentId, string agentName,
        string linkType = "Implements", string? note = null)
        => _taskLifecycle.LinkTaskToSpecAsync(taskId, specSectionId, agentId, agentName, linkType, note);

    /// <summary>
    /// Removes a spec-task link.
    /// </summary>
    public Task UnlinkTaskFromSpecAsync(string taskId, string specSectionId)
        => _taskQueries.UnlinkTaskFromSpecAsync(taskId, specSectionId);

    /// <summary>
    /// Gets all spec links for a task.
    /// </summary>
    public Task<List<SpecTaskLink>> GetSpecLinksForTaskAsync(string taskId)
        => _taskQueries.GetSpecLinksForTaskAsync(taskId);

    /// <summary>
    /// Gets all tasks linked to a spec section.
    /// </summary>
    public Task<List<SpecTaskLink>> GetTasksForSpecAsync(string specSectionId)
        => _taskQueries.GetTasksForSpecAsync(specSectionId);

    /// <summary>
    /// Gets tasks that have no spec links.
    /// </summary>
    public Task<List<TaskSnapshot>> GetUnlinkedTasksAsync()
        => _taskQueries.GetUnlinkedTasksAsync();

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
        string? userId = null, string? userName = null,
        string? userRole = null)
        => _messages.PostHumanMessageAsync(roomId, content, userId, userName, userRole);

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

        // ── Private Helpers ─────────────────────────────────────────

    private Task<List<BreakoutRoom>> GetAllBreakoutRoomsAsync()
        => _breakouts.GetAllBreakoutRoomsAsync();

    public Task<List<BreakoutRoom>> GetAgentSessionsAsync(string agentId)
        => _breakouts.GetAgentSessionsAsync(agentId);

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
