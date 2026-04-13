using AgentAcademy.Shared.Models;
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
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityBroadcaster _activityBus;
    private readonly SpecManager _specManager;
    private readonly BreakoutLifecycleService _breakoutLifecycle;
    private readonly AgentTurnRunner _turnRunner;
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
        AgentCatalogOptions catalog,
        ActivityBroadcaster activityBus,
        SpecManager specManager,
        BreakoutLifecycleService breakoutLifecycle,
        AgentTurnRunner turnRunner,
        ILogger<AgentOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _activityBus = activityBus;
        _specManager = specManager;
        _breakoutLifecycle = breakoutLifecycle;
        _turnRunner = turnRunner;
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
        if (!CrashRecoveryService.CurrentCrashDetected)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var crashRecovery = scope.ServiceProvider.GetRequiredService<CrashRecoveryService>();
        var result = await crashRecovery.RecoverFromCrashAsync(mainRoomId);

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
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        var pendingRoomIds = await roomService.GetRoomsWithPendingHumanMessagesAsync();

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
            var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
            var messageService = scope.ServiceProvider.GetRequiredService<MessageService>();
            var agentLocationService = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
            var taskItemService = scope.ServiceProvider.GetRequiredService<TaskItemService>();
            var activity = scope.ServiceProvider.GetRequiredService<ActivityPublisher>();
            var configService = scope.ServiceProvider.GetRequiredService<AgentConfigService>();

            // Check if conversation session needs rotation before this round
            if (round == 1)
            {
                try
                {
                    var sessionService = scope.ServiceProvider.GetRequiredService<ConversationSessionService>();
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

            var room = await roomService.GetRoomAsync(roomId);
            if (room is null) return;

            _logger.LogInformation(
                "Conversation round {Round}/{MaxRounds} for room {RoomId}",
                round, MaxRoundsPerTrigger, roomId);

            var ctx = await LoadRoundContextAsync(scope, roomId);

            // ── Planner phase ──
            var planner = FindPlanner();
            if (planner is not null)
                planner = await configService.GetEffectiveAgentAsync(planner);

            var plannerId = planner?.Id;

            if (planner is not null && ctx.ActiveSprintStage is not null
                && !SprintPreambles.IsRoleAllowedInStage(planner.Role, ctx.ActiveSprintStage))
            {
                _logger.LogInformation(
                    "Planner {PlannerName} excluded from sprint stage {Stage}",
                    planner.Name, ctx.ActiveSprintStage);
                planner = null;
            }

            var agentsToRun = new List<AgentDefinition>();

            if (planner is not null)
            {
                var freshRoom = await roomService.GetRoomAsync(roomId) ?? room;
                var taskItems = await taskItemService.GetActiveTaskItemsAsync();
                var plannerSuffix = "\n\nIMPORTANT: You are the lead planner. After your response, mention other agents "
                    + "by name if they should respond (e.g., '@Archimedes should review').\n"
                    + "If work needs to be done independently, use TASK ASSIGNMENT blocks to assign it:\n"
                    + "TASK ASSIGNMENT:\nAgent: @AgentName\nTitle: ...\nDescription: ...\nAcceptance Criteria:\n- ...\n";

                var plannerResult = await _turnRunner.RunAgentTurnAsync(
                    planner, scope, messageService, configService, activity,
                    freshRoom, roomId, ctx.SpecContext, taskItems, ctx.SessionSummary, ctx.SprintPreamble, plannerSuffix, ctx.SpecVersion);

                if (plannerResult.IsNonPass)
                {
                    hadNonPassResponse = true;
                    foreach (var a in AgentResponseParser.ParseTaggedAgents(_catalog.Agents, plannerResult.Response))
                    {
                        if (a.Id != plannerResult.Agent.Id) agentsToRun.Add(a);
                    }
                }
            }

            // ── Fallback to idle agents if nobody was tagged ──
            if (agentsToRun.Count == 0)
            {
                agentsToRun.AddRange(
                    (await GetIdleAgentsInRoomAsync(agentLocationService, roomId))
                        .Where(a => a.Id != plannerId)
                        .Take(3));
            }

            if (ctx.ActiveSprintStage is not null)
                agentsToRun = SprintPreambles.FilterByStageRoster(agentsToRun, ctx.ActiveSprintStage, a => a.Role);

            // ── Run agents sequentially so each sees prior responses ──
            foreach (var catalogAgent in agentsToRun)
            {
                if (_stopped) break;

                var currentRoom = await roomService.GetRoomAsync(roomId);
                if (currentRoom is null) break;

                var location = await agentLocationService.GetAgentLocationAsync(catalogAgent.Id);
                if (location?.State == AgentState.Working) continue;

                var result = await _turnRunner.RunAgentTurnAsync(
                    catalogAgent, scope, messageService, configService, activity,
                    currentRoom, roomId, ctx.SpecContext,
                    sessionSummary: ctx.SessionSummary, sprintPreamble: ctx.SprintPreamble, specVersion: ctx.SpecVersion);

                if (result.IsNonPass) hadNonPassResponse = true;
            }

            _logger.LogInformation(
                "Conversation round {Round} finished for room {RoomId}", round, roomId);

            if (!hadNonPassResponse || _stopped) break;

            var updatedRoom = await roomService.GetRoomAsync(roomId);
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
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        var messageService = scope.ServiceProvider.GetRequiredService<MessageService>();
        var agentLocationService = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var activity = scope.ServiceProvider.GetRequiredService<ActivityPublisher>();
        var configService = scope.ServiceProvider.GetRequiredService<AgentConfigService>();

        var catalogAgent = _catalog.Agents.FirstOrDefault(
            a => string.Equals(a.Id, recipientAgentId, StringComparison.OrdinalIgnoreCase));

        if (catalogAgent is null)
        {
            _logger.LogWarning("DM round: agent {AgentId} not found in catalog", recipientAgentId);
            return;
        }

        var agent = await configService.GetEffectiveAgentAsync(catalogAgent);

        // If agent is in a breakout room, forward DMs there instead
        var location = await agentLocationService.GetAgentLocationAsync(agent.Id);
        if (location?.State == AgentState.Working && location.BreakoutRoomId is not null)
        {
            var dms = await messageService.GetDirectMessagesForAgentAsync(agent.Id, limit: 5);
            if (dms.Count > 0)
            {
                foreach (var dm in dms)
                {
                    await messageService.PostBreakoutMessageAsync(
                        location.BreakoutRoomId,
                        "system", "System", "System",
                        $"📩 Direct message from {dm.SenderName}: {dm.Content}");
                }
                await messageService.AcknowledgeDirectMessagesAsync(agent.Id, dms.Select(m => m.Id).ToList());
            }
            _logger.LogInformation(
                "DM round: agent {AgentName} is in breakout room. DM posted to breakout context.",
                agent.Name);
            return;
        }

        var roomId = location?.RoomId;
        if (roomId is null)
        {
            var rooms = await roomService.GetRoomsAsync();
            roomId = rooms.FirstOrDefault()?.Id ?? "main";
        }

        var room = await roomService.GetRoomAsync(roomId);
        if (room is null) return;

        var ctx = await LoadRoundContextAsync(scope, roomId);

        await _turnRunner.RunAgentTurnAsync(
            catalogAgent, scope, messageService, configService, activity,
            room, roomId, ctx.SpecContext,
            sessionSummary: ctx.SessionSummary, sprintPreamble: ctx.SprintPreamble, specVersion: ctx.SpecVersion);

        _logger.LogInformation("DM round completed for agent {AgentName}", agent.Name);
    }

    // ── AGENT HELPERS ───────────────────────────────────────────

    private AgentDefinition? FindPlanner() =>
        _catalog.Agents.FirstOrDefault(a => a.Role == "Planner");

    private async Task<List<AgentDefinition>> GetIdleAgentsInRoomAsync(
        AgentLocationService agentLocationService, string roomId)
    {
        var result = new List<AgentDefinition>();
        foreach (var agent in _catalog.Agents)
        {
            var loc = await agentLocationService.GetAgentLocationAsync(agent.Id);
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

    // ── ROUND CONTEXT ──────────────────────────────────────────

    /// <summary>
    /// Immutable snapshot of shared per-round context. Data only — no services.
    /// Each field soft-fails to null independently so one failure cannot cascade.
    /// </summary>
    private record RoundContext(
        string? SpecContext,
        string? SpecVersion,
        string? SessionSummary,
        string? SprintPreamble,
        string? ActiveSprintStage);

    /// <summary>
    /// Loads the shared context needed by both conversation rounds and DM rounds.
    /// Each field fails independently to null with a logged warning.
    /// </summary>
    private async Task<RoundContext> LoadRoundContextAsync(IServiceScope scope, string roomId)
    {
        string? specContext = null;
        string? specVersion = null;
        string? sessionSummary = null;
        string? sprintPreamble = null;
        string? activeSprintStage = null;

        try
        {
            specContext = await _specManager.LoadSpecContextAsync();
            var versionInfo = await _specManager.GetSpecVersionAsync();
            specVersion = versionInfo?.Version;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load spec context for room {RoomId}", roomId);
        }

        try
        {
            var sessionService = scope.ServiceProvider.GetRequiredService<ConversationSessionQueryService>();
            sessionSummary = await sessionService.GetSessionContextAsync(roomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session context for room {RoomId}", roomId);
        }

        try
        {
            var (preamble, stage) = await LoadSprintContextAsync(scope);
            sprintPreamble = preamble;
            activeSprintStage = stage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load sprint context for room {RoomId}", roomId);
        }

        return new(specContext, specVersion, sessionSummary, sprintPreamble, activeSprintStage);
    }

    private record SprintContext(string? Preamble, string? ActiveStage);

    private async Task<SprintContext> LoadSprintContextAsync(IServiceScope scope)
    {
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        var sessionService = scope.ServiceProvider.GetRequiredService<ConversationSessionQueryService>();
        var sprintService = scope.ServiceProvider.GetRequiredService<SprintService>();
        var artifactService = scope.ServiceProvider.GetRequiredService<SprintArtifactService>();

        var workspacePath = await roomService.GetActiveWorkspacePathAsync();
        if (workspacePath is null) return new(null, null);

        var sprint = await sprintService.GetActiveSprintAsync(workspacePath);
        if (sprint is null) return new(null, null);

        var priorContext = await sessionService.GetSprintContextAsync(sprint.Id);

        string? overflowContent = null;
        if (sprint.CurrentStage == "Intake" && sprint.OverflowFromSprintId is not null)
        {
            var overflowArtifacts = await artifactService.GetSprintArtifactsAsync(sprint.Id);
            var overflow = overflowArtifacts.FirstOrDefault(a => a.Type == "OverflowRequirements");
            overflowContent = overflow?.Content;
        }

        var preamble = SprintPreambles.BuildPreamble(
            sprint.Number, sprint.CurrentStage, priorContext, overflowContent);

        return new(preamble, sprint.CurrentStage);
    }
}
