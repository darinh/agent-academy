using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Drives the multi-agent conversation lifecycle: queue-based message
/// processing, conversation rounds, breakout room workflows, and review
/// cycles. Ported from v1 TypeScript CollaborationOrchestrator.
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentExecutor _executor;
    private readonly ActivityBroadcaster _activityBus;
    private readonly SpecManager _specManager;
    private readonly CommandPipeline _commandPipeline;
    private readonly GitService _gitService;
    private readonly WorktreeService _worktreeService;
    private readonly BreakoutLifecycleService _breakoutLifecycle;
    private readonly ILogger<AgentOrchestrator> _logger;

    private readonly Queue<QueueItem> _queue = new();
    private readonly object _lock = new();
    private bool _processing;
    private volatile bool _stopped;

    private record QueueItem(string RoomId, string? TargetAgentId = null);

    /// <summary>Returns the current number of items in the processing queue (for testing/diagnostics).</summary>
    internal int QueueDepth { get { lock (_lock) { return _queue.Count; } } }

    public AgentOrchestrator(
        IServiceScopeFactory scopeFactory,
        IAgentExecutor executor,
        ActivityBroadcaster activityBus,
        SpecManager specManager,
        CommandPipeline commandPipeline,
        GitService gitService,
        WorktreeService worktreeService,
        BreakoutLifecycleService breakoutLifecycle,
        ILogger<AgentOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _executor = executor;
        _activityBus = activityBus;
        _specManager = specManager;
        _commandPipeline = commandPipeline;
        _gitService = gitService;
        _worktreeService = worktreeService;
        _breakoutLifecycle = breakoutLifecycle;
        _logger = logger;
    }

    /// <summary>Signals the orchestrator to stop processing.</summary>
    public void Stop()
    {
        _stopped = true;
        _breakoutLifecycle.Stop();
    }

    public async Task HandleStartupRecoveryAsync(string mainRoomId)
    {
        if (!WorkspaceRuntime.CurrentCrashDetected)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var result = await runtime.RecoverFromCrashAsync(mainRoomId);

        _logger.LogWarning(
            "Startup crash recovery ran for main room {RoomId}: {BreakoutCount} breakouts closed, {AgentCount} lingering agents reset",
            mainRoomId, result.ClosedBreakoutRooms, result.ResetWorkingAgents);
    }

    /// <summary>
    /// Scans for rooms with unanswered human messages and re-enqueues them.
    /// Call on every startup to recover queue state lost during shutdown or crash.
    /// </summary>
    public async Task ReconstructQueueAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var pendingRoomIds = await runtime.GetRoomsWithPendingHumanMessagesAsync();

        if (pendingRoomIds.Count == 0)
        {
            _logger.LogInformation("Queue reconstruction: no rooms with pending human messages");
            return;
        }

        lock (_lock)
        {
            foreach (var roomId in pendingRoomIds)
            {
                _queue.Enqueue(new QueueItem(roomId));
            }
        }

        _logger.LogInformation(
            "Queue reconstruction: re-enqueued {Count} room(s) with pending human messages: {RoomIds}",
            pendingRoomIds.Count, string.Join(", ", pendingRoomIds));

        _ = ProcessQueueAsync();
    }

    // ── PUBLIC ENTRY POINT ──────────────────────────────────────

    /// <summary>
    /// Enqueues a room for processing after a human message arrives.
    /// Processing is serialized — only one room is handled at a time.
    /// </summary>
    public void HandleHumanMessage(string roomId)
    {
        lock (_lock) { _queue.Enqueue(new QueueItem(roomId)); }
        _ = ProcessQueueAsync();
    }

    /// <summary>
    /// Triggers an immediate round for a specific agent after receiving a DM.
    /// Finds the agent's current room and runs only that agent.
    /// </summary>
    public void HandleDirectMessage(string recipientAgentId)
    {
        lock (_lock)
        {
            // Dedupe: skip if a DM trigger for this agent is already queued
            if (_queue.Any(q => q.TargetAgentId == recipientAgentId))
                return;
            _queue.Enqueue(new QueueItem(RoomId: "", TargetAgentId: recipientAgentId));
        }
        _ = ProcessQueueAsync();
    }

    // ── QUEUE ───────────────────────────────────────────────────

    private async Task ProcessQueueAsync()
    {
        lock (_lock)
        {
            if (_processing) return;
            _processing = true;
        }

        try
        {
            while (!_stopped)
            {
                QueueItem? item;
                lock (_lock)
                {
                    if (!_queue.TryDequeue(out item))
                    {
                        // Atomically clear processing flag while still holding the lock.
                        // Any concurrent HandleHumanMessage that enqueued after the last
                        // dequeue will see _processing == false and start a new loop.
                        _processing = false;
                        return;
                    }
                }

                try
                {
                    if (item.TargetAgentId is not null)
                    {
                        await RunDirectMessageRoundAsync(item.TargetAgentId);
                    }
                    else
                    {
                        await RunConversationRoundAsync(item.RoomId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Orchestrator failed for {Item}", item);
                }
            }
        }
        finally
        {
            lock (_lock) { _processing = false; }
        }
    }

    // ── CONVERSATION ROUND (MC room) ────────────────────────────

    private const int MaxRoundsPerTrigger = 3;

    private async Task RunConversationRoundAsync(string roomId)
    {
        for (int round = 1; round <= MaxRoundsPerTrigger; round++)
        {
            bool hadNonPassResponse = false;

            using var scope = _scopeFactory.CreateScope();
            var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
            var configService = scope.ServiceProvider.GetRequiredService<AgentConfigService>();
            var sessionService = scope.ServiceProvider.GetRequiredService<ConversationSessionService>();

            // Check if conversation session needs rotation before this round
            if (round == 1)
            {
                try
                {
                    var rotated = await sessionService.CheckAndRotateAsync(roomId);
                    if (rotated)
                        _logger.LogInformation(
                            "Conversation session rotated for room {RoomId} before round 1", roomId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session rotation check failed for room {RoomId}", roomId);
                }
            }

            var room = await runtime.GetRoomAsync(roomId);
            if (room is null) return;

            _logger.LogInformation(
                "Conversation round {Round}/{MaxRounds} for room {RoomId}",
                round, MaxRoundsPerTrigger, roomId);

            // Load spec context once for all prompts in this round
            var specContext = await _specManager.LoadSpecContextAsync();

            // Load session summary for context continuity after epoch rotation
            string? sessionSummary = null;
            try
            {
                sessionSummary = await sessionService.GetSessionContextAsync(roomId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load session context for room {RoomId}", roomId);
            }

            // Load active sprint context (if any) for stage-aware prompts and roster filtering
            string? sprintPreamble = null;
            string? activeSprintStage = null;
            try
            {
                var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
                var workspacePath = await runtime.GetActiveWorkspacePathAsync();
                if (workspacePath is not null)
                {
                    var sprint = await sprintService.GetActiveSprintAsync(workspacePath);
                    if (sprint is not null)
                    {
                        activeSprintStage = sprint.CurrentStage;
                        var priorContext = await sessionService.GetSprintContextAsync(sprint.Id);

                        // Load overflow content when entering Intake with overflow from previous sprint
                        string? overflowContent = null;
                        if (sprint.CurrentStage == "Intake" && sprint.OverflowFromSprintId is not null)
                        {
                            var overflowArtifacts = await sprintService.GetSprintArtifactsAsync(sprint.Id);
                            var overflow = overflowArtifacts.FirstOrDefault(a => a.Type == "OverflowRequirements");
                            overflowContent = overflow?.Content;
                        }

                        sprintPreamble = SprintPreambles.BuildPreamble(
                            sprint.Number, sprint.CurrentStage, priorContext, overflowContent);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load sprint context for room {RoomId}", roomId);
            }

            var planner = FindPlanner(runtime);
            if (planner is not null)
                planner = await configService.GetEffectiveAgentAsync(planner);

            // Capture planner ID before potential exclusion so the idle-agent
            // fallback can still exclude the planner from its pool
            var plannerId = planner?.Id;

            // Skip planner if not allowed in current sprint stage
            if (planner is not null && activeSprintStage is not null
                && !SprintPreambles.IsRoleAllowedInStage(planner.Role, activeSprintStage))
            {
                _logger.LogInformation(
                    "Planner {PlannerName} excluded from sprint stage {Stage}",
                    planner.Name, activeSprintStage);
                planner = null;
            }

            var agentsToRun = new List<AgentDefinition>();

            // Step 1 — Run the planner first
            if (planner is not null)
            {
                await runtime.PublishThinkingAsync(planner, roomId);
                var plannerResponse = "";
                try
                {
                    var freshRoom = await runtime.GetRoomAsync(roomId) ?? room;
                    var taskItems = await runtime.GetActiveTaskItemsAsync();
                    var plannerMemories = await LoadAgentMemoriesAsync(planner.Id);
                    var plannerDms = await runtime.GetDirectMessagesForAgentAsync(planner.Id);
                    if (plannerDms.Count > 0)
                        await runtime.AcknowledgeDirectMessagesAsync(planner.Id, plannerDms.Select(m => m.Id).ToList());
                    var prompt = PromptBuilder.BuildConversationPrompt(planner, freshRoom, specContext, taskItems, plannerMemories, plannerDms, sessionSummary, sprintPreamble)
                        + "\n\nIMPORTANT: You are the lead planner. After your response, mention other agents "
                        + "by name if they should respond (e.g., '@Archimedes should review').\n"
                        + "If work needs to be done independently, use TASK ASSIGNMENT blocks to assign it:\n"
                        + "TASK ASSIGNMENT:\nAgent: @AgentName\nTitle: ...\nDescription: ...\nAcceptance Criteria:\n- ...\n";
                    plannerResponse = await RunAgentAsync(planner, prompt, roomId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Planner failed");
                }
                finally
                {
                    await runtime.PublishFinishedAsync(planner, roomId);
                }

                if (!string.IsNullOrWhiteSpace(plannerResponse) && !AgentResponseParser.IsPassResponse(plannerResponse)
                    && !AgentResponseParser.IsStubOfflineResponse(plannerResponse))
                {
                    hadNonPassResponse = true;

                    // Process commands and post remaining text
                    await ProcessAndPostAgentResponseAsync(runtime, planner, roomId, plannerResponse);

                    // Collect @-mentioned agents for the next step
                    foreach (var a in AgentResponseParser.ParseTaggedAgents(runtime.GetConfiguredAgents(), plannerResponse))
                    {
                        if (a.Id != planner.Id) agentsToRun.Add(a);
                    }

                    // Detect and handle task assignments
                    foreach (var assignment in AgentResponseParser.ParseTaskAssignments(plannerResponse))
                    {
                        await HandleTaskAssignmentAsync(runtime, roomId, assignment);
                    }
                }
            }

            // Step 2 — Fall back to idle agents if nobody was tagged
            if (agentsToRun.Count == 0)
            {
                agentsToRun.AddRange(
                    (await GetIdleAgentsInRoomAsync(runtime, roomId))
                        .Where(a => a.Id != plannerId)
                        .Take(3));
            }

            // Filter agents by sprint stage roster (if active sprint)
            if (activeSprintStage is not null)
            {
                agentsToRun = SprintPreambles.FilterByStageRoster(
                    agentsToRun, activeSprintStage, a => a.Role);
            }

            // Step 3 — Run agents sequentially so each sees the previous response
            foreach (var catalogAgent in agentsToRun)
            {
                if (_stopped) break;

                var currentRoom = await runtime.GetRoomAsync(roomId);
                if (currentRoom is null) break;

                var agent = await configService.GetEffectiveAgentAsync(catalogAgent);

                // Skip agents that are already working in a breakout room
                var location = await runtime.GetAgentLocationAsync(agent.Id);
                if (location?.State == AgentState.Working) continue;

                await runtime.PublishThinkingAsync(agent, roomId);
                var response = "";
                try
                {
                    var agentMemories = await LoadAgentMemoriesAsync(agent.Id);
                    var agentDms = await runtime.GetDirectMessagesForAgentAsync(agent.Id);
                    if (agentDms.Count > 0)
                        await runtime.AcknowledgeDirectMessagesAsync(agent.Id, agentDms.Select(m => m.Id).ToList());
                    var prompt = PromptBuilder.BuildConversationPrompt(agent, currentRoom, specContext, memories: agentMemories, directMessages: agentDms, sessionSummary: sessionSummary, sprintPreamble: sprintPreamble);
                    response = await RunAgentAsync(agent, prompt, roomId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Agent {AgentName} failed", agent.Name);
                }
                finally
                {
                    await runtime.PublishFinishedAsync(agent, roomId);
                }

                if (!string.IsNullOrWhiteSpace(response) && !AgentResponseParser.IsPassResponse(response)
                    && !AgentResponseParser.IsStubOfflineResponse(response))
                {
                    hadNonPassResponse = true;

                    // Process commands and post remaining text
                    await ProcessAndPostAgentResponseAsync(runtime, agent, roomId, response);

                    foreach (var assignment in AgentResponseParser.ParseTaskAssignments(response))
                    {
                        if (await TryHandleTaskAssignmentWithGatingAsync(runtime, agent, roomId, assignment))
                            await HandleTaskAssignmentAsync(runtime, roomId, assignment);
                    }
                }
            }

            _logger.LogInformation(
                "Conversation round {Round} finished for room {RoomId}", round, roomId);

            // Continue with another round only if:
            // 1. Agents produced non-PASS responses (conversation is progressing)
            // 2. The room still has an active task (not just casual chat)
            // 3. We haven't hit the cap
            if (!hadNonPassResponse || _stopped) break;

            var updatedRoom = await runtime.GetRoomAsync(roomId);
            if (updatedRoom?.ActiveTask is null) break;

            if (round < MaxRoundsPerTrigger)
            {
                _logger.LogInformation(
                    "Non-PASS responses in room with active task; starting round {NextRound}/{MaxRounds}",
                    round + 1, MaxRoundsPerTrigger);
            }
        }
    }

    // ── DM ROUND ────────────────────────────────────────────────

    /// <summary>
    /// Runs a targeted round for a specific agent after receiving a DM.
    /// Only the recipient agent runs, with DMs injected into their context.
    /// </summary>
    private async Task RunDirectMessageRoundAsync(string recipientAgentId)
    {
        _logger.LogInformation("DM round for agent {AgentId}", recipientAgentId);

        using var scope = _scopeFactory.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var configService = scope.ServiceProvider.GetRequiredService<AgentConfigService>();

        // Find the recipient agent in catalog
        var agents = runtime.GetConfiguredAgents();
        var catalogAgent = agents.FirstOrDefault(
            a => string.Equals(a.Id, recipientAgentId, StringComparison.OrdinalIgnoreCase));

        if (catalogAgent is null)
        {
            _logger.LogWarning("DM round: agent {AgentId} not found in catalog", recipientAgentId);
            return;
        }

        var agent = await configService.GetEffectiveAgentAsync(catalogAgent);

        // Find the agent's current room
        var location = await runtime.GetAgentLocationAsync(agent.Id);
        if (location?.State == AgentState.Working && location.BreakoutRoomId is not null)
        {
            // Agent is in a breakout room — post all unread DMs as breakout messages
            var dms = await runtime.GetDirectMessagesForAgentAsync(agent.Id, limit: 5);
            if (dms.Count > 0)
            {
                foreach (var dm in dms)
                {
                    await runtime.PostBreakoutMessageAsync(
                        location.BreakoutRoomId,
                        "system", "System", "System",
                        $"📩 Direct message from {dm.SenderName}: {dm.Content}");
                }
                await runtime.AcknowledgeDirectMessagesAsync(agent.Id, dms.Select(m => m.Id).ToList());
            }
            _logger.LogInformation(
                "DM round: agent {AgentName} is in breakout room. DM posted to breakout context.",
                agent.Name);
            return;
        }

        var roomId = location?.RoomId;
        if (roomId is null)
        {
            var rooms = await runtime.GetRoomsAsync();
            roomId = rooms.FirstOrDefault()?.Id ?? "main";
        }

        var room = await runtime.GetRoomAsync(roomId);
        if (room is null) return;

        var specContext = await _specManager.LoadSpecContextAsync();
        var agentMemories = await LoadAgentMemoriesAsync(agent.Id);
        var directMessages = await runtime.GetDirectMessagesForAgentAsync(agent.Id);
        if (directMessages.Count > 0)
            await runtime.AcknowledgeDirectMessagesAsync(agent.Id, directMessages.Select(m => m.Id).ToList());
        string? dmSessionSummary = null;
        string? dmSprintPreamble = null;
        try
        {
            var dmSessionService = scope.ServiceProvider.GetRequiredService<ConversationSessionService>();
            dmSessionSummary = await dmSessionService.GetSessionContextAsync(roomId);

            // Inject sprint context into DMs so the agent has stage awareness
            var dmSprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
            var workspacePath = await runtime.GetActiveWorkspacePathAsync();
            if (workspacePath is not null)
            {
                var sprint = await dmSprintService.GetActiveSprintAsync(workspacePath);
                if (sprint is not null)
                {
                    var priorContext = await dmSessionService.GetSprintContextAsync(sprint.Id);

                    string? overflowContent = null;
                    if (sprint.CurrentStage == "Intake" && sprint.OverflowFromSprintId is not null)
                    {
                        var overflowArtifacts = await dmSprintService.GetSprintArtifactsAsync(sprint.Id);
                        var overflow = overflowArtifacts.FirstOrDefault(a => a.Type == "OverflowRequirements");
                        overflowContent = overflow?.Content;
                    }

                    dmSprintPreamble = SprintPreambles.BuildPreamble(
                        sprint.Number, sprint.CurrentStage, priorContext, overflowContent);
                }
            }
        }
        catch { /* non-critical */ }

        await runtime.PublishThinkingAsync(agent, roomId);
        var response = "";
        try
        {
            var prompt = PromptBuilder.BuildConversationPrompt(agent, room, specContext,
                memories: agentMemories, directMessages: directMessages,
                sessionSummary: dmSessionSummary, sprintPreamble: dmSprintPreamble);
            response = await RunAgentAsync(agent, prompt, roomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DM round: agent {AgentName} failed", agent.Name);
        }
        finally
        {
            await runtime.PublishFinishedAsync(agent, roomId);
        }

        if (!string.IsNullOrWhiteSpace(response) && !AgentResponseParser.IsPassResponse(response)
            && !AgentResponseParser.IsStubOfflineResponse(response))
        {
            await ProcessAndPostAgentResponseAsync(runtime, agent, roomId, response);

            foreach (var assignment in AgentResponseParser.ParseTaskAssignments(response))
            {
                if (await TryHandleTaskAssignmentWithGatingAsync(runtime, agent, roomId, assignment))
                    await HandleTaskAssignmentAsync(runtime, roomId, assignment);
            }
        }

        _logger.LogInformation("DM round completed for agent {AgentName}", agent.Name);
    }

    // ── TASK ASSIGNMENT GATING ──────────────────────────────────

    /// <summary>
    /// Checks whether an agent is allowed to create a task of the given type.
    /// Non-planners can only create Bug tasks. Other types are converted into a
    /// proposal message posted to the room. Returns true if the assignment should proceed.
    /// </summary>
    private async Task<bool> TryHandleTaskAssignmentWithGatingAsync(
        WorkspaceRuntime runtime, AgentDefinition agent, string roomId, ParsedTaskAssignment assignment)
    {
        // Planners can create any task type
        if (string.Equals(agent.Role, "Planner", StringComparison.OrdinalIgnoreCase))
            return true;

        // Non-planners can file bug reports
        if (assignment.Type == TaskType.Bug)
            return true;

        // Non-planners: convert to a proposal instead
        _logger.LogInformation(
            "Agent {AgentName} ({Role}) proposed task '{Title}' — only planners can create non-bug tasks",
            agent.Name, agent.Role, assignment.Title);

        await runtime.PostSystemStatusAsync(roomId,
            $"💡 **Task proposal from {agent.Name}**: \"{assignment.Title}\"\n" +
            $"{assignment.Description}\n\n" +
            $"_Only planners can create tasks. Aristotle, please review and assign if appropriate._");

        return false;
    }

    // ── TASK ASSIGNMENT PARSING ─────────────────────────────────
    private async Task HandleTaskAssignmentAsync(
        WorkspaceRuntime runtime, string roomId, ParsedTaskAssignment assignment)
    {
        var allAgents = runtime.GetConfiguredAgents();
        var agent = allAgents.FirstOrDefault(a =>
            a.Name.Equals(assignment.Agent, StringComparison.OrdinalIgnoreCase) ||
            a.Id.Equals(assignment.Agent, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            _logger.LogWarning("Task assignment references unknown agent: {Agent}", assignment.Agent);
            return;
        }

        // Prevent concurrent breakout rooms for the same agent
        var location = await runtime.GetAgentLocationAsync(agent.Id);
        if (location?.State == AgentState.Working)
        {
            _logger.LogWarning(
                "Agent {AgentName} is already in breakout room {BreakoutId} — skipping assignment '{Title}'",
                agent.Name, location.BreakoutRoomId, assignment.Title);
            await runtime.PostSystemStatusAsync(roomId,
                $"⚠️ {agent.Name} is already working on a task. Assignment \"{assignment.Title}\" will wait until they finish.");
            return;
        }

        var descriptionWithCriteria = assignment.Description
            + (assignment.Criteria.Count > 0
                ? "\n\nAcceptance Criteria:\n" + string.Join("\n", assignment.Criteria.Select(c => $"- {c}"))
                : "");

        var brName = $"BR: {assignment.Title}";
        var br = await runtime.CreateBreakoutRoomAsync(roomId, agent.Id, brName);

        string? taskBranch = null;
        string? taskId = null;
        TaskItem? taskItem = null;
        string? worktreePath = null;
        try
        {
            taskBranch = await _gitService.CreateTaskBranchAsync(assignment.Title);

            if (!await _gitService.BranchExistsAsync(taskBranch))
                throw new InvalidOperationException($"Branch '{taskBranch}' was not created");

            await _gitService.ReturnToDevelopAsync(taskBranch);

            // Create a worktree for isolated work when a workspace is available
            var workspacePath = await runtime.GetActiveWorkspacePathAsync();
            if (workspacePath is not null && taskBranch is not null)
            {
                try
                {
                    var worktree = await _worktreeService.CreateWorktreeAsync(taskBranch);
                    worktreePath = worktree.Path;
                    _logger.LogInformation(
                        "Created worktree for task branch {Branch} at {Path}",
                        taskBranch, worktree.Path);
                }
                catch (Exception wtEx)
                {
                    _logger.LogWarning(wtEx,
                        "Failed to create worktree for {Branch} — agent will work on shared checkout",
                        taskBranch);
                }
            }

            taskItem = await runtime.CreateTaskItemAsync(
                assignment.Title, descriptionWithCriteria,
                agent.Id, roomId, br.Id);

            taskId = await runtime.EnsureTaskForBreakoutAsync(
                br.Id, assignment.Title, descriptionWithCriteria, agent.Id, roomId,
                PromptBuilder.BuildAssignmentPlanContent(assignment), taskBranch);

            var task = await runtime.GetTaskAsync(taskId);
            var planContent = !string.IsNullOrWhiteSpace(task?.CurrentPlan)
                ? task.CurrentPlan
                : PromptBuilder.BuildAssignmentPlanContent(assignment);
            await runtime.SetPlanAsync(br.Id, planContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create task branch for {Title} — cleaning up", assignment.Title);

            // Each cleanup step is independent — one failure must not prevent the others
            try
            {
                await runtime.CloseBreakoutRoomAsync(br.Id, BreakoutRoomCloseReason.Cancelled);
            }
            catch (Exception closeEx)
            {
                _logger.LogWarning(closeEx, "Failed to close breakout room {BreakoutId}", br.Id);
            }

            // Clean up the orphaned task if it was created before the failure
            if (taskId is not null)
            {
                try
                {
                    await runtime.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Cancelled);
                }
                catch (Exception cancelEx)
                {
                    _logger.LogWarning(cancelEx, "Failed to cancel orphaned task {TaskId}", taskId);
                }
            }

            // Clean up the orphaned task item if it was created before the failure
            if (taskItem is not null)
            {
                try
                {
                    await runtime.UpdateTaskItemStatusAsync(taskItem.Id, Shared.Models.TaskItemStatus.Rejected);
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Failed to cancel orphaned task item {TaskItemId}", taskItem.Id);
                }
            }

            // Clean up the git branch if it was created before the failure.
            // ReturnToDevelopAsync may have failed, leaving us on the task branch —
            // must checkout develop first since git can't delete the checked-out branch.
            if (taskBranch is not null)
            {
                // Remove worktree first — git can't delete a branch checked out in a worktree
                if (worktreePath is not null)
                {
                    try
                    {
                        await _worktreeService.RemoveWorktreeAsync(taskBranch);
                    }
                    catch (Exception wtEx)
                    {
                        _logger.LogWarning(wtEx, "Failed to remove worktree for orphaned branch {Branch}", taskBranch);
                    }
                }

                try
                {
                    await _gitService.ReturnToDevelopAsync(taskBranch);
                }
                catch { /* best-effort — may already be on develop */ }

                try
                {
                    await _gitService.DeleteBranchAsync(taskBranch);
                }
                catch (Exception branchEx)
                {
                    _logger.LogWarning(branchEx, "Failed to delete orphaned branch {Branch}", taskBranch);
                }
            }

            try
            {
                await runtime.PostSystemStatusAsync(roomId,
                    $"⚠️ Failed to set up branch for \"{assignment.Title}\". Breakout cancelled.");
            }
            catch { /* best-effort notification */ }
            return;
        }

        await runtime.PostSystemStatusAsync(roomId,
            $"📋 {agent.Name} has been assigned \"{assignment.Title}\" and is heading to breakout room \"{brName}\" on branch `{taskBranch}`.");

        _ = Task.Run(() => _breakoutLifecycle.RunBreakoutLifecycleAsync(
            br.Id, agent.Id, roomId, agent, taskBranch, worktreePath));
    }

    // ── AGENT HELPERS ───────────────────────────────────────────

    private static AgentDefinition? FindPlanner(WorkspaceRuntime runtime) =>
        runtime.GetConfiguredAgents().FirstOrDefault(a => a.Role == "Planner");

    private static async Task<List<AgentDefinition>> GetIdleAgentsInRoomAsync(
        WorkspaceRuntime runtime, string roomId)
    {
        var result = new List<AgentDefinition>();
        foreach (var agent in runtime.GetConfiguredAgents())
        {
            var loc = await runtime.GetAgentLocationAsync(agent.Id);
            if (loc is not null &&
                loc.RoomId == roomId &&
                (loc.State == AgentState.Idle ||
                 loc.State == AgentState.InRoom ||
                 loc.State == AgentState.Presenting))
            {
                result.Add(agent);
            }
        }
        return result;
    }

    private async Task<string> RunAgentAsync(
        AgentDefinition agent, string prompt, string roomId, string? workspacePath = null)
    {
        try
        {
            return await _executor.RunAsync(agent, prompt, roomId, workspacePath);
        }
        catch (AgentQuotaExceededException ex)
        {
            _logger.LogWarning(
                "Agent {AgentName} quota exceeded ({QuotaType}): {Message}",
                agent.Name, ex.QuotaType, ex.Message);
            return $"⚠️ **{agent.Name} is temporarily paused** — {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Agent {AgentName} was cancelled", agent.Name);
            return "";
        }
    }

    // ── MESSAGE POSTING ─────────────────────────────────────────

    /// <summary>
    /// Processes commands from an agent response, posts the remaining text,
    /// and posts command results as a system message for context visibility.
    /// </summary>
    private async Task ProcessAndPostAgentResponseAsync(
        WorkspaceRuntime runtime, AgentDefinition agent, string roomId, string response)
    {
        // Run response through command pipeline
        var pipelineResult = await ProcessCommandsAsync(agent, response, roomId);

        // Post the remaining text (with commands stripped) as the agent's message
        var textToPost = pipelineResult.RemainingText;
        if (!string.IsNullOrWhiteSpace(textToPost) && !AgentResponseParser.IsPassResponse(textToPost))
        {
            await PostAgentMessageAsync(runtime, agent, roomId, textToPost);
        }

        // Post command results as system message so subsequent prompts include them
        var formattedResults = CommandPipeline.FormatResultsForContext(pipelineResult.Results);
        if (!string.IsNullOrEmpty(formattedResults))
        {
            await runtime.PostSystemStatusAsync(roomId, formattedResults);
        }
    }

    /// <summary>
    /// Runs agent response text through the command pipeline within a new scope.
    /// </summary>
    private async Task<CommandPipelineResult> ProcessCommandsAsync(
        AgentDefinition agent, string responseText, string roomId, string? workingDirectory = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            return await _commandPipeline.ProcessResponseAsync(
                agent.Id, responseText, roomId, agent, scope.ServiceProvider, workingDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Command processing failed for agent {AgentId}", agent.Id);
            return new CommandPipelineResult(new List<CommandEnvelope>(), responseText, ProcessingFailed: true);
        }
    }

    /// <summary>
    /// Loads an agent's persisted memories from the database,
    /// including shared memories from all agents.
    /// </summary>
    private async Task<List<AgentMemory>> LoadAgentMemoriesAsync(string agentId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var now = DateTime.UtcNow;

            var entities = await db.AgentMemories
                .Where(m => m.AgentId == agentId || m.Category == "shared")
                .Where(m => m.ExpiresAt == null || m.ExpiresAt > now)
                .OrderBy(m => m.Category)
                .ThenBy(m => m.Key)
                .ToListAsync();

            // Update LastAccessedAt for all loaded memories (best-effort, batched)
            if (entities.Count > 0)
            {
                try
                {
                    foreach (var group in entities.GroupBy(e => e.AgentId))
                    {
                        var aid = group.Key;
                        var keyList = group.Select(g => g.Key).ToList();
                        var placeholders = string.Join(", ", keyList.Select((_, i) => $"{{{i + 2}}}"));
                        var sql = $"UPDATE agent_memories SET LastAccessedAt = {{0}} WHERE AgentId = {{1}} AND Key IN ({placeholders})";
                        var parameters = new List<object> { now, aid };
                        parameters.AddRange(keyList);
                        await db.Database.ExecuteSqlRawAsync(sql, parameters.ToArray());
                    }
                }
                catch { /* best-effort */ }
            }

            return entities.Select(e => new AgentMemory(
                e.AgentId, e.Category, e.Key, e.Value, e.CreatedAt, e.UpdatedAt,
                e.LastAccessedAt, e.ExpiresAt
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load memories for agent {AgentId}", agentId);
            return new List<AgentMemory>();
        }
    }

    private async Task PostAgentMessageAsync(
        WorkspaceRuntime runtime, AgentDefinition agent, string roomId, string content)
    {
        try
        {
            await runtime.PostMessageAsync(new PostMessageRequest(
                RoomId: roomId,
                SenderId: agent.Id,
                Content: content,
                Kind: AgentResponseParser.InferMessageKind(agent.Role)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post message for {AgentId}", agent.Id);
        }
    }
}
