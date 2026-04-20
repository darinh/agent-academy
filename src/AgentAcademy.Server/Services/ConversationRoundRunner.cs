using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Executes conversation rounds for a room: runs the planner, selects agents,
/// filters by sprint stage, and runs agents sequentially. Each round gets a
/// fresh DI scope to ensure clean DbContext state.
/// Extracted from AgentOrchestrator to isolate round logic from queue management.
/// </summary>
public sealed class ConversationRoundRunner : IConversationRoundRunner
{
    private const int MaxRoundsPerTrigger = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentTurnRunner _turnRunner;
    private readonly ILogger<ConversationRoundRunner> _logger;

    public ConversationRoundRunner(
        IServiceScopeFactory scopeFactory,
        IAgentCatalog catalog,
        IAgentTurnRunner turnRunner,
        ILogger<ConversationRoundRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _turnRunner = turnRunner;
        _logger = logger;
    }

    /// <summary>
    /// Runs up to <see cref="MaxRoundsPerTrigger"/> conversation rounds for
    /// the given room. Stops early if all agents PASS or no active task remains.
    /// </summary>
    public async Task RunRoundsAsync(string roomId, CancellationToken cancellationToken = default)
    {
        for (int round = 1; round <= MaxRoundsPerTrigger; round++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            bool hadNonPassResponse = false;

            using var scope = _scopeFactory.CreateScope();
            var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
            var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
            var agentLocationService = scope.ServiceProvider.GetRequiredService<IAgentLocationService>();
            var taskItemService = scope.ServiceProvider.GetRequiredService<ITaskItemService>();
            var activity = scope.ServiceProvider.GetRequiredService<IActivityPublisher>();
            var configService = scope.ServiceProvider.GetRequiredService<IAgentConfigService>();
            var contextLoader = scope.ServiceProvider.GetRequiredService<RoundContextLoader>();

            var room = await roomService.GetRoomAsync(roomId);
            if (room is null) return;

            _logger.LogInformation(
                "Conversation round {Round}/{MaxRounds} for room {RoomId}",
                round, MaxRoundsPerTrigger, roomId);

            var ctx = await contextLoader.LoadAsync(roomId);

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
                if (cancellationToken.IsCancellationRequested) break;

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

            if (!hadNonPassResponse || cancellationToken.IsCancellationRequested) break;

            var updatedRoom = await roomService.GetRoomAsync(roomId);
            if (updatedRoom?.ActiveTask is null) break;

            if (round < MaxRoundsPerTrigger)
            {
                _logger.LogInformation(
                    "Non-PASS responses in room with active task; starting round {NextRound}/{MaxRounds}",
                    round + 1, MaxRoundsPerTrigger);
            }
        }

        // Rotate session AFTER all rounds complete so the triggering human
        // message stays visible to agents during this run. Rotation before
        // rounds would archive the session containing the human message,
        // making agents see an empty conversation and PASS.
        try
        {
            using var rotationScope = _scopeFactory.CreateScope();
            var sessionService = rotationScope.ServiceProvider.GetRequiredService<IConversationSessionService>();
            var rotated = await sessionService.CheckAndRotateAsync(roomId);
            if (rotated)
                _logger.LogInformation(
                    "Conversation session rotated for room {RoomId} after rounds completed", roomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-round session rotation check failed for room {RoomId}", roomId);
        }
    }

    private AgentDefinition? FindPlanner() =>
        _catalog.Agents.FirstOrDefault(a => a.Role == "Planner");

    private async Task<List<AgentDefinition>> GetIdleAgentsInRoomAsync(
        IAgentLocationService agentLocationService, string roomId)
    {
        // Capture each candidate's last-activity timestamp so we can return
        // them in LRU order (oldest UpdatedAt first). Without this, callers
        // using `.Take(N)` always pick the same first N agents in catalog
        // order, starving any agent positioned later in the catalog.
        var candidates = new List<(AgentDefinition Agent, DateTime LastActivity)>();
        foreach (var agent in _catalog.Agents)
        {
            var loc = await agentLocationService.GetAgentLocationAsync(agent.Id);
            if (loc is not null &&
                loc.RoomId == roomId &&
                (loc.State == AgentState.Idle ||
                 loc.State == AgentState.InRoom ||
                 loc.State == AgentState.Presenting))
            {
                candidates.Add((agent, loc.UpdatedAt));
            }
        }

        // Stable sort by last-activity ascending (least-recently-active first).
        // OrderBy is stable in .NET, so ties (e.g., agents with identical
        // UpdatedAt timestamps from initialization) preserve catalog order —
        // keeping behavior deterministic for tests while still rotating once
        // any agent runs and bumps its timestamp.
        return candidates
            .OrderBy(c => c.LastActivity)
            .Select(c => c.Agent)
            .ToList();
    }
}
