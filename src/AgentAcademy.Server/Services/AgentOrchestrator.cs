using System.Text.RegularExpressions;
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
    /// <summary>Cap on the number of agents that can be tagged in one round.</summary>
    private const int MaxTaggedAgents = 6;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentExecutor _executor;
    private readonly ActivityBroadcaster _activityBus;
    private readonly SpecManager _specManager;
    private readonly CommandPipeline _commandPipeline;
    private readonly GitService _gitService;
    private readonly ILogger<AgentOrchestrator> _logger;

    private readonly Queue<QueueItem> _queue = new();
    private readonly object _lock = new();
    private bool _processing;
    private volatile bool _stopped;

    private record QueueItem(string RoomId, string? TargetAgentId = null);

    public AgentOrchestrator(
        IServiceScopeFactory scopeFactory,
        IAgentExecutor executor,
        ActivityBroadcaster activityBus,
        SpecManager specManager,
        CommandPipeline commandPipeline,
        GitService gitService,
        ILogger<AgentOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _executor = executor;
        _activityBus = activityBus;
        _specManager = specManager;
        _commandPipeline = commandPipeline;
        _gitService = gitService;
        _logger = logger;
    }

    /// <summary>Signals the orchestrator to stop processing.</summary>
    public void Stop() => _stopped = true;

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
            var specContext = _specManager.LoadSpecContext();

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

            var planner = FindPlanner(runtime);
            if (planner is not null)
                planner = await configService.GetEffectiveAgentAsync(planner);
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
                    var prompt = BuildConversationPrompt(planner, freshRoom, specContext, taskItems, plannerMemories, plannerDms, sessionSummary)
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

                if (!string.IsNullOrWhiteSpace(plannerResponse) && !IsPassResponse(plannerResponse)
                    && !IsStubOfflineResponse(plannerResponse))
                {
                    hadNonPassResponse = true;

                    // Process commands and post remaining text
                    await ProcessAndPostAgentResponseAsync(runtime, planner, roomId, plannerResponse);

                    // Collect @-mentioned agents for the next step
                    foreach (var a in ParseTaggedAgents(runtime, plannerResponse))
                    {
                        if (a.Id != planner.Id) agentsToRun.Add(a);
                    }

                    // Detect and handle task assignments
                    foreach (var assignment in ParseTaskAssignments(plannerResponse))
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
                        .Where(a => a.Id != planner?.Id)
                        .Take(3));
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
                    var prompt = BuildConversationPrompt(agent, currentRoom, specContext, memories: agentMemories, directMessages: agentDms, sessionSummary: sessionSummary);
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

                if (!string.IsNullOrWhiteSpace(response) && !IsPassResponse(response)
                    && !IsStubOfflineResponse(response))
                {
                    hadNonPassResponse = true;

                    // Process commands and post remaining text
                    await ProcessAndPostAgentResponseAsync(runtime, agent, roomId, response);

                    foreach (var assignment in ParseTaskAssignments(response))
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

        var specContext = _specManager.LoadSpecContext();
        var agentMemories = await LoadAgentMemoriesAsync(agent.Id);
        var directMessages = await runtime.GetDirectMessagesForAgentAsync(agent.Id);
        if (directMessages.Count > 0)
            await runtime.AcknowledgeDirectMessagesAsync(agent.Id, directMessages.Select(m => m.Id).ToList());
        string? dmSessionSummary = null;
        try
        {
            var dmSessionService = scope.ServiceProvider.GetRequiredService<ConversationSessionService>();
            dmSessionSummary = await dmSessionService.GetSessionContextAsync(roomId);
        }
        catch { /* non-critical */ }

        await runtime.PublishThinkingAsync(agent, roomId);
        var response = "";
        try
        {
            var prompt = BuildConversationPrompt(agent, room, specContext,
                memories: agentMemories, directMessages: directMessages, sessionSummary: dmSessionSummary);
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

        if (!string.IsNullOrWhiteSpace(response) && !IsPassResponse(response)
            && !IsStubOfflineResponse(response))
        {
            await ProcessAndPostAgentResponseAsync(runtime, agent, roomId, response);

            foreach (var assignment in ParseTaskAssignments(response))
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

    /// <summary>
    /// Parses TASK ASSIGNMENT: blocks from an agent's response text.
    /// </summary>
    internal static List<ParsedTaskAssignment> ParseTaskAssignments(string content)
    {
        var assignments = new List<ParsedTaskAssignment>();

        var blocks = Regex.Split(content, @"TASK ASSIGNMENT:", RegexOptions.IgnoreCase);
        foreach (var block in blocks.Skip(1))
        {
            var agentMatch = Regex.Match(block, @"Agent:\s*@?(\S+)", RegexOptions.IgnoreCase);
            var titleMatch = Regex.Match(block, @"Title:\s*(.+)", RegexOptions.IgnoreCase);
            var descMatch = Regex.Match(block, @"Description:\s*([\s\S]*?)(?=Acceptance Criteria:|Type:|TASK ASSIGNMENT:|$)", RegexOptions.IgnoreCase);
            var criteriaMatch = Regex.Match(block, @"Acceptance Criteria:\s*([\s\S]*?)(?=Type:|TASK ASSIGNMENT:|$)", RegexOptions.IgnoreCase);
            var typeMatch = Regex.Match(block, @"Type:\s*(\S+)", RegexOptions.IgnoreCase);

            if (!agentMatch.Success || !titleMatch.Success) continue;

            var criteria = new List<string>();
            if (criteriaMatch.Success)
            {
                foreach (var line in criteriaMatch.Groups[1].Value.Split('\n'))
                {
                    var trimmed = Regex.Replace(line, @"^[-*]\s*", "").Trim();
                    if (!string.IsNullOrEmpty(trimmed)) criteria.Add(trimmed);
                }
            }

            var taskType = TaskType.Feature;
            if (typeMatch.Success)
                Enum.TryParse(typeMatch.Groups[1].Value.Trim(), ignoreCase: true, out taskType);

            assignments.Add(new ParsedTaskAssignment(
                Agent: agentMatch.Groups[1].Value.Trim(),
                Title: titleMatch.Groups[1].Value.Trim(),
                Description: descMatch.Success ? descMatch.Groups[1].Value.Trim() : titleMatch.Groups[1].Value.Trim(),
                Criteria: criteria,
                Type: taskType));
        }

        return assignments;
    }

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

        var descriptionWithCriteria = assignment.Description
            + (assignment.Criteria.Count > 0
                ? "\n\nAcceptance Criteria:\n" + string.Join("\n", assignment.Criteria.Select(c => $"- {c}"))
                : "");

        var brName = $"BR: {assignment.Title}";
        var br = await runtime.CreateBreakoutRoomAsync(roomId, agent.Id, brName);

        await runtime.CreateTaskItemAsync(
            assignment.Title, descriptionWithCriteria,
            agent.Id, roomId, br.Id);

        string? taskBranch = null;
        string? taskId = null;
        try
        {
            taskId = await runtime.EnsureTaskForBreakoutAsync(
                br.Id, assignment.Title, descriptionWithCriteria, agent.Id, roomId,
                BuildAssignmentPlanContent(assignment));

            taskBranch = await _gitService.CreateTaskBranchAsync(assignment.Title);
            await runtime.UpdateTaskBranchAsync(taskId, taskBranch);
            var task = await runtime.GetTaskAsync(taskId);
            var planContent = !string.IsNullOrWhiteSpace(task?.CurrentPlan)
                ? task.CurrentPlan
                : BuildAssignmentPlanContent(assignment);
            await runtime.SetPlanAsync(br.Id, planContent);
            await _gitService.ReturnToDevelopAsync(taskBranch);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create task branch for {Title} — closing breakout", assignment.Title);
            await runtime.CloseBreakoutRoomAsync(br.Id, BreakoutRoomCloseReason.Cancelled);

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

            await runtime.PostSystemStatusAsync(roomId,
                $"⚠️ Failed to set up branch for \"{assignment.Title}\". Breakout cancelled.");
            return;
        }

        await runtime.PostSystemStatusAsync(roomId,
            $"📋 {agent.Name} has been assigned \"{assignment.Title}\" and is heading to breakout room \"{brName}\" on branch `{taskBranch}`.");

        _ = Task.Run(async () =>
        {
            try { await RunBreakoutLoopAsync(br.Id, agent.Id, taskBranch); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Breakout loop failed for {AgentName} in {BreakoutId}", agent.Name, br.Id);
            }
        });
    }

    // ── BREAKOUT ROOM ───────────────────────────────────────────

    private async Task RunBreakoutLoopAsync(string breakoutRoomId, string agentId, string? taskBranch = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var configService = scope.ServiceProvider.GetRequiredService<AgentConfigService>();
        var sessionService = scope.ServiceProvider.GetRequiredService<ConversationSessionService>();

        var catalogAgent = runtime.GetConfiguredAgents().FirstOrDefault(a => a.Id == agentId);
        if (catalogAgent is null) return;
        var agent = await configService.GetEffectiveAgentAsync(catalogAgent);

        var br = await runtime.GetBreakoutRoomAsync(breakoutRoomId);
        if (br is null) return;

        _logger.LogInformation("Starting breakout loop for {AgentName} in {BreakoutName}", agent.Name, br.Name);

        var tasks = await runtime.GetBreakoutTaskItemsAsync(breakoutRoomId);
        await runtime.PostBreakoutMessageAsync(
            breakoutRoomId, "system", "LocalAgentHost", "System",
            BuildTaskBrief(agent, tasks, taskBranch));

        for (var round = 1; ; round++)
        {
            if (_stopped) break;

            // Check breakout session rotation before each round
            try
            {
                await sessionService.CheckAndRotateAsync(breakoutRoomId, "Breakout");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Session rotation check failed for breakout {BreakoutId}", breakoutRoomId);
            }

            var currentBr = await runtime.GetBreakoutRoomAsync(breakoutRoomId);
            if (currentBr is null || currentBr.Status != RoomStatus.Active) break;

            _logger.LogInformation("Breakout round {Round} for {AgentName}",
                round, agent.Name);

            var response = "";
            var isStubOffline = false;
            if (taskBranch != null)
                await _gitService.AcquireRoundLockAsync();
            try
            {
                if (taskBranch != null)
                    await _gitService.EnsureBranchInternalAsync(taskBranch);

                try
                {
                    var breakoutMemories = await LoadAgentMemoriesAsync(agent.Id);
                    var breakoutDms = await runtime.GetDirectMessagesForAgentAsync(agent.Id);
                    if (breakoutDms.Count > 0)
                        await runtime.AcknowledgeDirectMessagesAsync(agent.Id, breakoutDms.Select(m => m.Id).ToList());
                    var breakoutSummary = await sessionService.GetSessionContextAsync(breakoutRoomId);
                    var prompt = BuildBreakoutPrompt(agent, currentBr, round, breakoutMemories, breakoutDms, breakoutSummary);
                    response = await RunAgentAsync(agent, prompt, breakoutRoomId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Breakout agent {AgentName} failed in round {Round}", agent.Name, round);
                    continue;
                }

                // Process commands while still on the task branch so git
                // operations (SHELL git-commit, etc.) target the correct branch.
                if (!string.IsNullOrWhiteSpace(response))
                {
                    isStubOffline = IsStubOfflineResponse(response);
                    if (!isStubOffline)
                        await ProcessCommandsAsync(agent, response, breakoutRoomId);
                }
            }
            finally
            {
                if (taskBranch != null)
                {
                    await _gitService.ReturnToDevelopInternalAsync(taskBranch);
                    _gitService.ReleaseRoundLock();
                }
            }

            // DB-only operations after branch switch-back
            if (isStubOffline)
            {
                _logger.LogWarning(
                    "Agent {AgentName} returned stub offline notice in breakout — aborting loop",
                    agent.Name);
                await runtime.PostBreakoutMessageAsync(
                    breakoutRoomId, "system", "LocalAgentHost", "System",
                    $"⚠️ {agent.Name} is offline. Breakout suspended until the Copilot SDK is reconnected.");
                return; // Don't fall through to HandleBreakoutCompleteAsync
            }

            if (!string.IsNullOrWhiteSpace(response))
            {
                await runtime.PostBreakoutMessageAsync(
                    breakoutRoomId, agent.Id, agent.Name, agent.Role, response);
            }

            var report = ParseWorkReport(response);
            if (report is not null && Regex.IsMatch(report.Status, @"complete", RegexOptions.IgnoreCase))
            {
                var latestTasks = await runtime.GetBreakoutTaskItemsAsync(breakoutRoomId);
                foreach (var task in latestTasks)
                {
                    try { await runtime.UpdateTaskItemStatusAsync(task.Id, TaskItemStatus.Done, report.Evidence); }
                    catch { /* ok */ }
                }
                await HandleBreakoutCompleteAsync(runtime, configService, breakoutRoomId, br.ParentRoomId);
                return;
            }
        }

        // Loop exited — agent stopped or room closed/recalled
        _logger.LogInformation("Breakout loop ended for {AgentName}", agent.Name);

        // If the breakout was recalled (archived externally), skip completion/review flow
        var finalBr = await runtime.GetBreakoutRoomAsync(breakoutRoomId);
        if (finalBr is null || finalBr.Status != RoomStatus.Active)
        {
            _logger.LogInformation("Breakout {BreakoutId} was recalled or archived — skipping completion flow", breakoutRoomId);
            return;
        }

        await HandleBreakoutCompleteAsync(runtime, configService, breakoutRoomId, br.ParentRoomId);
    }

    // ── BREAKOUT COMPLETION / REVIEW ────────────────────────────

    private async Task HandleBreakoutCompleteAsync(
        WorkspaceRuntime runtime, AgentConfigService configService,
        string breakoutRoomId, string parentRoomId)
    {
        var br = await runtime.GetBreakoutRoomAsync(breakoutRoomId);
        if (br is null) return;

        var catalogAgent = runtime.GetConfiguredAgents().FirstOrDefault(a => a.Id == br.AssignedAgentId);
        if (catalogAgent is null) return;
        var agent = await configService.GetEffectiveAgentAsync(catalogAgent);

        _logger.LogInformation("Breakout complete: {AgentName} returning from {BreakoutName}", agent.Name, br.Name);

        // Move agent to MC in "presenting" state
        await runtime.MoveAgentAsync(agent.Id, parentRoomId, AgentState.Presenting);
        await runtime.PostSystemStatusAsync(parentRoomId,
            $"🎯 {agent.Name} has completed work in \"{br.Name}\" and is presenting results.");

        // Post the last agent message (work report) into the MC room
        var agentMessages = br.RecentMessages.Where(m => m.SenderId == agent.Id).ToList();
        var lastMessage = agentMessages.LastOrDefault();
        if (lastMessage is not null)
        {
            try
            {
                await runtime.PostMessageAsync(new PostMessageRequest(
                    RoomId: parentRoomId,
                    SenderId: agent.Id,
                    Content: lastMessage.Content,
                    Kind: MessageKind.Response));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post work report for {AgentName}", agent.Name);
            }
        }

        // Check if this breakout has a linked task with a branch
        var reviewTask = await runtime.TransitionBreakoutTaskToInReviewAsync(breakoutRoomId);

        if (reviewTask?.BranchName is not null)
        {
            // Branch-based task: skip auto-review, use manual APPROVE_TASK → MERGE_TASK flow
            await runtime.PostSystemStatusAsync(parentRoomId,
                $"📋 Task \"{reviewTask.Title}\" is now **InReview** on branch `{reviewTask.BranchName}`. " +
                $"Use APPROVE_TASK to approve, then MERGE_TASK to merge into develop.");
            await FinalizeBreakoutAsync(runtime, breakoutRoomId);
        }
        else
        {
            // Legacy path: automated review cycle
            var verdict = await RunReviewCycleAsync(runtime, configService, parentRoomId, agent, lastMessage?.Content ?? "");

            var isApproved = verdict is null ||
                Regex.IsMatch(verdict.Verdict, @"^\s*APPROVED", RegexOptions.IgnoreCase);

            if (!isApproved)
            {
                await HandleReviewRejectionAsync(runtime, breakoutRoomId, parentRoomId, agent, br);
            }
            else
            {
                await FinalizeBreakoutAsync(runtime, breakoutRoomId);
            }
        }
    }

    private async Task<ParsedReviewVerdict?> RunReviewCycleAsync(
        WorkspaceRuntime runtime, AgentConfigService configService,
        string parentRoomId, AgentDefinition presentingAgent, string workReport)
    {
        var catalogReviewer = FindReviewer(runtime);
        if (catalogReviewer is null) return null;
        var reviewer = await configService.GetEffectiveAgentAsync(catalogReviewer);

        await runtime.PublishThinkingAsync(reviewer, parentRoomId);
        var reviewResponse = "";
        try
        {
            var room = await runtime.GetRoomAsync(parentRoomId);
            if (room is null) return null;
            var prompt = BuildReviewPrompt(reviewer, presentingAgent.Name, workReport,
                _specManager.LoadSpecContext());
            reviewResponse = await RunAgentAsync(reviewer, prompt, parentRoomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reviewer failed");
            return null;
        }
        finally
        {
            await runtime.PublishFinishedAsync(reviewer, parentRoomId);
        }

        if (string.IsNullOrWhiteSpace(reviewResponse) || IsPassResponse(reviewResponse))
            return null;

        // If reviewer returned offline notice, skip the review cycle entirely
        if (IsStubOfflineResponse(reviewResponse))
        {
            _logger.LogWarning("Reviewer returned stub offline notice — skipping review cycle");
            return null;
        }

        try
        {
            await runtime.PostMessageAsync(new PostMessageRequest(
                RoomId: parentRoomId,
                SenderId: reviewer.Id,
                Content: reviewResponse,
                Kind: MessageKind.Review));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post review");
        }

        return ParseReviewVerdict(reviewResponse);
    }

    private async Task HandleReviewRejectionAsync(
        WorkspaceRuntime runtime, string breakoutRoomId, string parentRoomId,
        AgentDefinition agent, BreakoutRoom br)
    {
        // Resolve session service for epoch context
        using var fixScope = _scopeFactory.CreateScope();
        var sessionService = fixScope.ServiceProvider.GetRequiredService<ConversationSessionService>();

        await runtime.PostSystemStatusAsync(parentRoomId,
            $"🔄 {agent.Name} is returning to \"{br.Name}\" to address review feedback.");
        await runtime.MoveAgentAsync(agent.Id, parentRoomId, AgentState.Working, breakoutRoomId);

        // Post review feedback into the breakout room
        var room = await runtime.GetRoomAsync(parentRoomId);
        var reviewMessage = room?.RecentMessages
            .Where(m => m.Kind == MessageKind.Review)
            .LastOrDefault();

        if (reviewMessage is not null)
        {
            await runtime.PostBreakoutMessageAsync(
                breakoutRoomId, "system", "LocalAgentHost", "System",
                $"Review feedback:\n{reviewMessage.Content}\n\nPlease address the findings and produce an updated WORK REPORT.");
        }

        // Grant fix rounds — no cap, agent works until done
        for (var round = 1; ; round++)
        {
            if (_stopped) break;
            var updatedBr = await runtime.GetBreakoutRoomAsync(breakoutRoomId);
            if (updatedBr is null || updatedBr.Status != RoomStatus.Active) break;

            var response = "";
            try
            {
                var fixMemories = await LoadAgentMemoriesAsync(agent.Id);
                var fixDms = await runtime.GetDirectMessagesForAgentAsync(agent.Id);
                if (fixDms.Count > 0)
                    await runtime.AcknowledgeDirectMessagesAsync(agent.Id, fixDms.Select(m => m.Id).ToList());
                var fixSummary = await sessionService.GetSessionContextAsync(breakoutRoomId);
                response = await RunAgentAsync(
                    agent, BuildBreakoutPrompt(agent, updatedBr, round, fixMemories, fixDms, fixSummary),
                    breakoutRoomId);
            }
            catch { continue; }

            if (!string.IsNullOrWhiteSpace(response))
            {
                // Bail if agent returned stub offline notice
                if (IsStubOfflineResponse(response))
                {
                    _logger.LogWarning(
                        "Agent {AgentName} returned stub offline notice in fix round — aborting",
                        agent.Name);
                    return; // Don't fall through to FinalizeBreakoutAsync
                }

                // Record full agent response in breakout room for activity trail
                await runtime.PostBreakoutMessageAsync(
                    breakoutRoomId, agent.Id, agent.Name, agent.Role, response);

                // Process commands from fix-round response
                await ProcessCommandsAsync(agent, response, breakoutRoomId);
            }

            var report = ParseWorkReport(response);
            if (report is not null && Regex.IsMatch(report.Status, @"complete", RegexOptions.IgnoreCase))
            {
                var latestTasks = await runtime.GetBreakoutTaskItemsAsync(breakoutRoomId);
                foreach (var task in latestTasks)
                {
                    try { await runtime.UpdateTaskItemStatusAsync(task.Id, TaskItemStatus.Done, report.Evidence); }
                    catch { /* ok */ }
                }
                break;
            }
        }

        await FinalizeBreakoutAsync(runtime, breakoutRoomId);
    }

    private async Task FinalizeBreakoutAsync(WorkspaceRuntime runtime, string breakoutRoomId)
    {
        try
        {
            await runtime.CloseBreakoutRoomAsync(breakoutRoomId, BreakoutRoomCloseReason.Completed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close breakout room {BreakoutId}", breakoutRoomId);
        }
    }

    // ── PARSING ─────────────────────────────────────────────────

    /// <summary>
    /// Parses a WORK REPORT: block from agent response text.
    /// Returns null if no report block is found.
    /// </summary>
    internal static ParsedWorkReport? ParseWorkReport(string content)
    {
        var match = Regex.Match(content, @"WORK REPORT:([\s\S]*?)(?=$|\nTASK ASSIGNMENT:)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var block = match.Groups[1].Value;
        var statusMatch = Regex.Match(block, @"Status:\s*(.+)", RegexOptions.IgnoreCase);
        var filesMatch = Regex.Match(block, @"Files?:\s*([\s\S]*?)(?=Evidence:|$)", RegexOptions.IgnoreCase);
        var evidenceMatch = Regex.Match(block, @"Evidence:\s*([\s\S]*?)$", RegexOptions.IgnoreCase);

        var files = new List<string>();
        if (filesMatch.Success)
        {
            foreach (var line in filesMatch.Groups[1].Value.Split('\n'))
            {
                var trimmed = Regex.Replace(line, @"^[-*]\s*", "").Trim();
                if (!string.IsNullOrEmpty(trimmed)) files.Add(trimmed);
            }
        }

        return new ParsedWorkReport(
            Status: statusMatch.Success ? statusMatch.Groups[1].Value.Trim() : "unknown",
            Files: files,
            Evidence: evidenceMatch.Success ? evidenceMatch.Groups[1].Value.Trim() : "");
    }

    /// <summary>
    /// Parses a REVIEW: block from reviewer response text.
    /// Returns null if no review block is found.
    /// </summary>
    internal static ParsedReviewVerdict? ParseReviewVerdict(string content)
    {
        var match = Regex.Match(content, @"REVIEW:([\s\S]*?)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var block = match.Groups[1].Value;
        var verdictMatch = Regex.Match(block, @"(?:Verdict|Status|Decision):\s*(.+)", RegexOptions.IgnoreCase);
        var findingsMatch = Regex.Match(block, @"(?:Findings?|Issues?|Comments?):\s*([\s\S]*?)$", RegexOptions.IgnoreCase);

        var findings = new List<string>();
        if (findingsMatch.Success)
        {
            foreach (var line in findingsMatch.Groups[1].Value.Split('\n'))
            {
                var trimmed = Regex.Replace(line, @"^[-*]\s*", "").Trim();
                if (!string.IsNullOrEmpty(trimmed)) findings.Add(trimmed);
            }
        }

        return new ParsedReviewVerdict(
            Verdict: verdictMatch.Success
                ? verdictMatch.Groups[1].Value.Trim()
                : block.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "",
            Findings: findings);
    }

    // ── AGENT HELPERS ───────────────────────────────────────────

    private static AgentDefinition? FindPlanner(WorkspaceRuntime runtime) =>
        runtime.GetConfiguredAgents().FirstOrDefault(a => a.Role == "Planner");

    private static AgentDefinition? FindReviewer(WorkspaceRuntime runtime) =>
        runtime.GetConfiguredAgents().FirstOrDefault(a => a.Role == "Reviewer");

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

    private List<AgentDefinition> ParseTaggedAgents(WorkspaceRuntime runtime, string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return [];

        var allAgents = runtime.GetConfiguredAgents();
        var tagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AgentDefinition>();

        foreach (var agent in allAgents)
        {
            if (tagged.Contains(agent.Id)) continue;

            var namePattern = $@"@?{Regex.Escape(agent.Name)}\b";
            var idPattern = $@"@?{Regex.Escape(agent.Id)}\b";

            if (Regex.IsMatch(response, namePattern, RegexOptions.IgnoreCase) ||
                Regex.IsMatch(response, idPattern, RegexOptions.IgnoreCase))
            {
                tagged.Add(agent.Id);
                result.Add(agent);
            }
        }

        return result.Take(MaxTaggedAgents).ToList();
    }

    private async Task<string> RunAgentAsync(
        AgentDefinition agent, string prompt, string roomId)
    {
        try
        {
            return await _executor.RunAsync(agent, prompt, roomId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Agent {AgentName} was cancelled", agent.Name);
            return "";
        }
    }

    // ── PROMPT BUILDERS ─────────────────────────────────────────

    private static string BuildConversationPrompt(
        AgentDefinition agent, RoomSnapshot room, string? specContext,
        List<TaskItem>? activeTaskItems = null,
        List<AgentMemory>? memories = null,
        List<Data.Entities.MessageEntity>? directMessages = null,
        string? sessionSummary = null)
    {
        // Note: agent.StartupPrompt is NOT included here — it's already sent
        // as session priming in CopilotExecutor.GetOrCreateSessionEntryAsync.
        // Including it again would duplicate it in the SDK session context.
        var lines = new List<string>();

        // Inject session summary from previous epoch if available
        if (!string.IsNullOrEmpty(sessionSummary))
        {
            lines.Add("=== PREVIOUS CONVERSATION SUMMARY ===");
            lines.Add(sessionSummary);
            lines.Add("");
        }

        // Inject agent memories before room context
        if (memories is { Count: > 0 })
        {
            lines.Add("=== YOUR MEMORIES ===");
            foreach (var m in memories)
                lines.Add($"[{m.Category}] {m.Key}: {m.Value}");
            lines.Add("");
        }

        lines.Add("=== CURRENT ROOM CONTEXT ===");
        lines.Add($"Room: {room.Name}");

        if (room.ActiveTask is not null)
        {
            lines.Add("");
            lines.Add("=== TASK ===");
            lines.Add($"Title: {room.ActiveTask.Title}");
            lines.Add($"Description: {room.ActiveTask.Description}");
            if (!string.IsNullOrEmpty(room.ActiveTask.SuccessCriteria))
                lines.Add($"Success criteria: {room.ActiveTask.SuccessCriteria}");
        }

        if (activeTaskItems is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("=== IN-FLIGHT WORK ITEMS ===");
            foreach (var item in activeTaskItems)
            {
                var workspace = item.BreakoutRoomId is not null ? " [in workspace]" : "";
                lines.Add($"- [{item.Status}] \"{item.Title}\" → assigned to {item.AssignedTo}{workspace}");
            }
        }

        if (specContext is not null)
        {
            lines.Add("");
            lines.Add("=== PROJECT SPECIFICATION ===");
            lines.Add("The project maintains a living spec in specs/. Relevant sections:");
            lines.Add(specContext);
        }

        if (room.RecentMessages.Count > 0)
        {
            lines.Add("");
            lines.Add("=== RECENT CONVERSATION ===");
            foreach (var msg in room.RecentMessages.TakeLast(20))
            {
                lines.Add($"[{msg.SenderName} ({msg.SenderRole ?? msg.SenderKind.ToString()})]: {msg.Content}");
            }
        }

        if (directMessages is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("=== DIRECT MESSAGES ===");
            lines.Add("These are private messages only you can see. Reply via DM command if needed.");
            foreach (var dm in directMessages)
            {
                var direction = dm.SenderId == agent.Id
                    ? $"[DM to {dm.RecipientId}]"
                    : $"[DM from {dm.SenderName}]";
                lines.Add($"{direction}: {dm.Content}");
            }
        }

        lines.Add("");
        lines.Add("=== YOUR TURN ===");
        lines.Add($"You are {agent.Name} ({agent.Role}).");
        lines.Add("Respond naturally to the conversation. Be concise and actionable.");
        lines.Add("If you have nothing meaningful to contribute, reply with exactly: PASS");

        return string.Join("\n", lines);
    }

    private static string BuildBreakoutPrompt(AgentDefinition agent, BreakoutRoom br, int round,
        List<AgentMemory>? memories = null,
        List<Data.Entities.MessageEntity>? directMessages = null,
        string? sessionSummary = null)
    {
        // Note: agent.StartupPrompt is NOT included here — it's already sent
        // as session priming in CopilotExecutor.GetOrCreateSessionEntryAsync.
        var lines = new List<string>();

        // Inject session summary from previous epoch if available
        if (!string.IsNullOrEmpty(sessionSummary))
        {
            lines.Add("=== PREVIOUS WORK SUMMARY ===");
            lines.Add(sessionSummary);
            lines.Add("");
        }

        // Inject agent memories
        if (memories is { Count: > 0 })
        {
            lines.Add("=== YOUR MEMORIES ===");
            foreach (var m in memories)
                lines.Add($"[{m.Category}] {m.Key}: {m.Value}");
            lines.Add("");
        }

        lines.Add($"=== BREAKOUT ROOM: {br.Name} ===");
        lines.Add($"Round: {round}");

        if (br.Tasks.Count > 0)
        {
            lines.Add("");
            lines.Add("=== ASSIGNED TASKS ===");
            foreach (var task in br.Tasks)
            {
                lines.Add($"Task: {task.Title}");
                lines.Add($"Description: {task.Description}");
                lines.Add($"Status: {task.Status}");
                lines.Add("");
            }
        }

        if (br.RecentMessages.Count > 0)
        {
            lines.Add("=== WORK LOG ===");
            foreach (var msg in br.RecentMessages.TakeLast(10))
            {
                lines.Add($"[{msg.SenderName}]: {msg.Content}");
            }
        }

        if (directMessages is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("=== DIRECT MESSAGES ===");
            lines.Add("These are private messages only you can see. Reply via DM command if needed.");
            foreach (var dm in directMessages)
            {
                var direction = dm.SenderId == agent.Id
                    ? $"[DM to {dm.RecipientId}]"
                    : $"[DM from {dm.SenderName}]";
                lines.Add($"{direction}: {dm.Content}");
            }
        }

        lines.Add("");
        lines.Add("=== YOUR TURN ===");
        lines.Add($"You are {agent.Name} ({agent.Role}).");
        lines.Add("Do the work described in your tasks. Create files, write code, execute commands.");
        lines.Add("When your work is complete, include a WORK REPORT block:");
        lines.Add("WORK REPORT:");
        lines.Add("Status: COMPLETE");
        lines.Add("Files: [list of created/modified files]");
        lines.Add("Evidence: [description of what was done and how it meets criteria]");

        return string.Join("\n", lines);
    }

    private static string BuildReviewPrompt(
        AgentDefinition reviewer, string agentName, string workReport, string? specContext)
    {
        var lines = new List<string> { reviewer.StartupPrompt, "" };
        lines.Add("=== REVIEW REQUEST ===");
        lines.Add($"{agentName} has completed work and is presenting their results.");
        lines.Add("");
        lines.Add("=== WORK REPORT ===");
        lines.Add(workReport);

        if (specContext is not null)
        {
            lines.Add("");
            lines.Add("=== SPEC SECTIONS (verify accuracy against delivered work) ===");
            lines.Add(specContext);
        }

        lines.Add("");
        lines.Add("=== YOUR TURN ===");
        lines.Add($"You are {reviewer.Name} ({reviewer.Role}).");
        lines.Add("Review the work report and provide your assessment.");
        lines.Add("End your review with a REVIEW: block:");
        lines.Add("REVIEW:");
        lines.Add("Verdict: APPROVED | NEEDS FIX");
        lines.Add("Findings:");
        lines.Add("- [list any issues or commendations]");
        if (specContext is not null)
        {
            lines.Add("Spec Accuracy:");
            lines.Add("- [PASS/FAIL] Do spec updates match the delivered implementation?");
            lines.Add("- [list any spec-code discrepancies found]");
        }

        return string.Join("\n", lines);
    }

    internal static string BuildAssignmentPlanContent(ParsedTaskAssignment assignment)
    {
        var lines = new List<string>
        {
            $"# {assignment.Title}",
            "",
            "## Objective",
            string.IsNullOrWhiteSpace(assignment.Description)
                ? assignment.Title
                : assignment.Description.Trim()
        };

        if (assignment.Criteria.Count > 0)
        {
            lines.Add("");
            lines.Add("## Acceptance Criteria");
            lines.AddRange(assignment.Criteria.Select(c => $"- {c}"));
        }

        return string.Join("\n", lines);
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
        if (!string.IsNullOrWhiteSpace(textToPost) && !IsPassResponse(textToPost))
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
        AgentDefinition agent, string responseText, string roomId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            return await _commandPipeline.ProcessResponseAsync(
                agent.Id, responseText, roomId, agent, scope.ServiceProvider);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Command processing failed for agent {AgentId}", agent.Id);
            return new CommandPipelineResult(new List<CommandEnvelope>(), responseText);
        }
    }

    /// <summary>
    /// Loads an agent's persisted memories from the database.
    /// </summary>
    private async Task<List<AgentMemory>> LoadAgentMemoriesAsync(string agentId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var entities = await db.AgentMemories
                .Where(m => m.AgentId == agentId)
                .OrderBy(m => m.Category)
                .ThenBy(m => m.Key)
                .ToListAsync();

            return entities.Select(e => new AgentMemory(
                e.AgentId, e.Category, e.Key, e.Value, e.CreatedAt, e.UpdatedAt
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
                Kind: InferMessageKind(agent.Role)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post message for {AgentId}", agent.Id);
        }
    }

    /// <summary>
    /// Maps an agent's role to the appropriate <see cref="MessageKind"/>.
    /// </summary>
    internal static MessageKind InferMessageKind(string role) => role switch
    {
        "Planner" => MessageKind.Coordination,
        "Architect" => MessageKind.Decision,
        "SoftwareEngineer" => MessageKind.Response,
        "Reviewer" => MessageKind.Review,
        "Validator" => MessageKind.Validation,
        "TechnicalWriter" => MessageKind.SpecChangeProposal,
        _ => MessageKind.Response,
    };

    /// <summary>
    /// Detects "pass" responses — short responses that indicate the agent
    /// has nothing meaningful to contribute.
    /// </summary>
    internal static bool IsPassResponse(string response)
    {
        var trimmed = response.Trim();
        return trimmed.Length < 30 &&
               (Regex.IsMatch(trimmed, @"PASS", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(trimmed, @"^N/A$", RegexOptions.IgnoreCase) ||
                trimmed == "No comment." ||
                trimmed == "Nothing to add.");
    }

    /// <summary>
    /// Detects StubExecutor offline responses. When the Copilot SDK is not
    /// connected, retrying will produce the same result — abort early instead
    /// of burning through all breakout/review rounds.
    /// </summary>
    internal static bool IsStubOfflineResponse(string response) =>
        response.Contains("is offline — the Copilot SDK is not connected", StringComparison.Ordinal);

    private static string BuildTaskBrief(AgentDefinition agent, List<TaskItem> tasks, string? taskBranch = null)
    {
        var lines = new List<string>
        {
            $"Task Brief for {agent.Name}",
            new('=', 40)
        };
        if (taskBranch != null)
            lines.Add($"Branch: {taskBranch}");
        foreach (var task in tasks)
        {
            lines.Add($"\nTask: {task.Title}");
            lines.Add($"Description: {task.Description}");
        }
        return string.Join("\n", lines);
    }
}

// ── Data Transfer Records ───────────────────────────────────────

/// <summary>A parsed TASK ASSIGNMENT: block.</summary>
internal record ParsedTaskAssignment(
    string Agent,
    string Title,
    string Description,
    List<string> Criteria,
    TaskType Type = TaskType.Feature);

/// <summary>A parsed WORK REPORT: block.</summary>
internal record ParsedWorkReport(
    string Status,
    List<string> Files,
    string Evidence);

/// <summary>A parsed REVIEW: verdict block.</summary>
internal record ParsedReviewVerdict(
    string Verdict,
    List<string> Findings);
