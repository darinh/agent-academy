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
    private readonly ISelfDriveDecisionService? _selfDriveDecision;
    private readonly ILogger<ConversationRoundRunner> _logger;

    public ConversationRoundRunner(
        IServiceScopeFactory scopeFactory,
        IAgentCatalog catalog,
        IAgentTurnRunner turnRunner,
        ILogger<ConversationRoundRunner> logger,
        ISelfDriveDecisionService? selfDriveDecision = null)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _turnRunner = turnRunner;
        _selfDriveDecision = selfDriveDecision;
        _logger = logger;
    }

    /// <summary>
    /// Runs up to <see cref="MaxRoundsPerTrigger"/> conversation rounds for
    /// the given room. Stops early if all agents PASS or no active task remains.
    /// </summary>
    public async Task<RoundRunOutcome> RunRoundsAsync(
        string roomId,
        bool wasSelfDriveContinuation = false,
        CancellationToken cancellationToken = default)
    {
        // Outer accumulators: any non-PASS across the inner loop, plus the
        // count of inner rounds we actually executed. Both feed RoundRunOutcome
        // so the SelfDriveDecisionService (P1.2 §13 step 5) can decide without
        // re-querying state.
        bool anyRoundHadNonPass = false;
        int innerRoundsExecuted = 0;
        // Sprint ID captured at the start of the first executed inner round
        // so the post-run counter bump is credited to the sprint that the
        // rounds actually ran for, not to whatever sprint happens to be
        // active when the bump runs (TOCTOU: sprint A could complete and
        // sprint B could become active mid-trigger; bumping B for A's work
        // would corrupt B's self-drive accounting).
        string? sprintIdAtRunStart = null;
        // We attempt capture exactly once on the first executed round. If
        // it succeeds (even with a null result — "no sprint at this
        // workspace") or throws, we don't try again. Retrying would
        // reintroduce the TOCTOU window: round 1 capture fails, sprint A
        // completes, sprint B becomes active, round 2 captures B, B gets
        // credited for A's prior round.
        bool captureAttempted = false;

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
            if (room is null)
            {
                // Mid-trigger room deletion (rare). Don't early-return —
                // earlier iterations may have bumped innerRoundsExecuted
                // and captured a sprint ID. Break to fall through to the
                // post-loop counter bump and outcome return so prior
                // accounting + non-PASS signal aren't silently discarded.
                // The rotation block (next) is wrapped in try/catch so the
                // missing room doesn't crash it.
                break;
            }

            _logger.LogInformation(
                "Conversation round {Round}/{MaxRounds} for room {RoomId}",
                round, MaxRoundsPerTrigger, roomId);

            // From here on, an inner round IS executing. Count it now so the
            // sprint-counter bump (after the loop) reflects rounds that
            // actually ran agents, even if a later step short-circuits.
            innerRoundsExecuted++;

            // Capture the active sprint exactly once, on the first executed
            // round. Gated by `captureAttempted` (NOT by `sprintIdAtRunStart
            // is null`) so a capture that legitimately yields null ("no
            // sprint here") or throws does not retry on round 2 — retrying
            // would reintroduce TOCTOU: round 1 capture fails, sprint A
            // completes, sprint B becomes active, round 2 captures B, B
            // gets credited for A's already-executed round.
            if (!captureAttempted)
            {
                captureAttempted = true;
                var roomServiceForSprint = scope.ServiceProvider.GetRequiredService<IRoomService>();
                var sprintServiceForCapture = scope.ServiceProvider.GetRequiredService<Contracts.ISprintService>();
                try
                {
                    var workspacePathAtStart = await roomServiceForSprint.GetWorkspacePathForRoomAsync(roomId);
                    if (!string.IsNullOrEmpty(workspacePathAtStart))
                    {
                        // Capture is best-effort and runs even if the trigger's
                        // CT is firing — a stale capture is safer than no capture.
                        var activeSprint = await sprintServiceForCapture.GetActiveSprintAsync(workspacePathAtStart);
                        sprintIdAtRunStart = activeSprint?.Id;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Pre-round sprint capture failed for room {RoomId}; counter bump will be skipped",
                        roomId);
                }
            }

            var ctx = await contextLoader.LoadAsync(roomId);

            // Resolve the room's workspace path once per round so it can be
            // threaded into every agent turn this round runs. This is the
            // upstream half of P1.9 blocker B's fix: without this, every
            // RunAgentTurnAsync call passes workspacePath=null and the SDK
            // tool wrappers fall back to FindProjectRoot()=develop checkout,
            // bypassing the worktree-isolation plumbing PR #169 added below.
            // Lookup is a single indexed FindAsync; safe to do per round.
            //
            // No try/catch on purpose (codex review): if the workspace lookup
            // throws, fail closed by letting the exception propagate. The
            // alternative (catch + null fallback) would silently route writes
            // to the develop checkout under transient DB failures —
            // re-introducing the exact P1.9 blocker B regression. A "no
            // workspace" room legitimately returns null without throwing
            // (RoomService.GetWorkspacePathForRoomAsync handles that
            // path internally), so this only fires on real DB errors.
            var roomWorkspacePath = await roomService.GetWorkspacePathForRoomAsync(roomId);

            // P1.9 blocker D: per-agent workspace resolver. When an agent has a
            // currently-claimed in-flight task with a worktree, route them into
            // that worktree so write_file/commit_changes target the worktree
            // rather than contaminating the develop checkout. Resolved in this
            // scope so the resolver shares the round's DbContext. Optional so
            // unit tests that only stub the round-runner DI can still run; in
            // that case the per-agent override degrades to the room workspace
            // path (matching pre-P1.9-blocker-D behaviour).
            var workspaceResolver = scope.ServiceProvider.GetService<IAgentWorkspaceResolver>();

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

                var plannerWorkspacePath = workspaceResolver is not null
                    ? await workspaceResolver.ResolveAsync(planner.Id, roomId, roomWorkspacePath)
                    : roomWorkspacePath;

                var plannerResult = await _turnRunner.RunAgentTurnAsync(
                    planner, scope, messageService, configService, activity,
                    freshRoom, roomId, ctx.SpecContext, taskItems, ctx.SessionSummary, ctx.SprintPreamble, plannerSuffix, ctx.SpecVersion,
                    sprintIdAtRunStart, plannerWorkspacePath, cancellationToken);

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

                var agentWorkspacePath = workspaceResolver is not null
                    ? await workspaceResolver.ResolveAsync(catalogAgent.Id, roomId, roomWorkspacePath)
                    : roomWorkspacePath;

                var result = await _turnRunner.RunAgentTurnAsync(
                    catalogAgent, scope, messageService, configService, activity,
                    currentRoom, roomId, ctx.SpecContext,
                    sessionSummary: ctx.SessionSummary, sprintPreamble: ctx.SprintPreamble, specVersion: ctx.SpecVersion,
                    sprintId: sprintIdAtRunStart, workspacePath: agentWorkspacePath, cancellationToken: cancellationToken);

                if (result.IsNonPass) hadNonPassResponse = true;
            }

            _logger.LogInformation(
                "Conversation round {Round} finished for room {RoomId}", round, roomId);

            if (hadNonPassResponse) anyRoundHadNonPass = true;

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

        // Bump self-drive counters on the sprint that was active when this
        // trigger STARTED running rounds (P1.2 §13 step 3). Using the
        // captured sprint ID — not "active sprint right now" — is required
        // because a sprint A could complete and a sprint B could become
        // active during a long trigger; bumping B for A's rounds would
        // corrupt B's self-drive accounting. If A is no longer Active when
        // this fires, IncrementRoundCountersAsync's WHERE Status='Active'
        // guard makes the call a no-op (correct: A's totals are frozen at
        // completion). Fails open: counter-bump failure must not propagate
        // to the queue dispatcher because the trigger run already
        // succeeded. The decision service (§13 step 5) is the next caller
        // and reads the freshly-persisted counters before deciding to
        // enqueue a continuation. wasSelfDriveContinuation is hard-coded
        // false here — it will be threaded through from the queue item
        // when SystemContinuation is introduced in §13 step 7.
        if (sprintIdAtRunStart is not null)
        {
            try
            {
                using var counterScope = _scopeFactory.CreateScope();
                var sprintService = counterScope.ServiceProvider.GetRequiredService<Contracts.ISprintService>();
                await sprintService.IncrementRoundCountersAsync(
                    sprintIdAtRunStart,
                    innerRoundsExecuted,
                    wasSelfDriveContinuation: wasSelfDriveContinuation,
                    completedAt: DateTime.UtcNow,
                    // Pass CancellationToken.None: rounds already executed
                    // and counters MUST persist. If we forwarded the trigger
                    // CT, a cancellation mid-loop would cause EF's
                    // ExecuteUpdateAsync to throw OperationCanceledException
                    // before persisting, the catch below would swallow it
                    // as a warning, and self-drive accounting would
                    // permanently undercount executed rounds.
                    ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-round sprint counter bump failed for sprint {SprintId} (room {RoomId}, rounds={Rounds})",
                    sprintIdAtRunStart, roomId, innerRoundsExecuted);
            }
        }

        var roundOutcome = new RoundRunOutcome(anyRoundHadNonPass, innerRoundsExecuted);

        // Terminal-stage check: if the team has finished implementation work
        // and the ceremony chain can advance, fire the next transition. Runs
        // BEFORE the self-drive decision; if the driver took ANY action
        // (including Block), self-drive is skipped because (a)
        // StartedSelfEval/AdvancedToFinal/SteeredToFinal already woke the
        // rooms, so a continuation enqueue is redundant; (b) more importantly,
        // scheduling a continuation could trip the stage round cap
        // (Implementation: 20/20) immediately after StartedSelfEval, blocking
        // the just-started ceremony before the agent can produce the report.
        // See sprint-terminal-stage-handler-design.md §4.4 — the conditional
        // skip is critical, not optional. Fail-open inside the helper.
        var terminalAction = TerminalStageAction.NoOp;
        if (sprintIdAtRunStart is not null)
        {
            terminalAction = await InvokeTerminalStageHandlerAsync(
                sprintIdAtRunStart, CancellationToken.None);
        }

        // P1.2 §13 step 7–9: self-drive decision. Run AFTER the counter
        // bump so the decision service reads freshly-persisted counters.
        // Pass CancellationToken.None — the trigger CT is about to be
        // disposed; the decision service owns its own lifetime via the
        // orchestrator. Fail-open inside the helper. SKIP when the terminal
        // handler already steered the sprint (see comment above).
        if (terminalAction == TerminalStageAction.NoOp)
        {
            await InvokeSelfDriveDecisionAsync(
                roomId, sprintIdAtRunStart, roundOutcome, CancellationToken.None);
        }

        return roundOutcome;
    }

    /// <summary>
    /// Internal helper: invoke the terminal-stage handler after the counter
    /// bump and before the self-drive decision. Wrapped in a fail-open
    /// try/catch — a handler crash MUST NOT propagate; the round trigger has
    /// already succeeded. On any unexpected failure returns
    /// <see cref="TerminalStageAction.NoOp"/> so the self-drive decision still
    /// runs (safe-side default — preserves pre-driver behaviour when the
    /// driver itself is unavailable).
    /// </summary>
    private async Task<TerminalStageAction> InvokeTerminalStageHandlerAsync(
        string capturedSprintId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetService<ISprintTerminalStageHandler>();
            if (handler is null) return TerminalStageAction.NoOp;
            return await handler.AdvanceIfReadyAsync(capturedSprintId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Terminal-stage handler invocation failed for sprint {SprintId}; " +
                "self-drive decision will run as if NoOp",
                capturedSprintId);
            return TerminalStageAction.NoOp;
        }
    }

    /// <summary>
    /// Internal helper: invoke the self-drive decision service after the
    /// counter bump. Wrapped in a fail-open try/catch — a decision-service
    /// crash MUST NOT propagate; the round trigger has already succeeded.
    /// Public for testability via internals-visible-to or intentional reuse.
    /// </summary>
    private async Task InvokeSelfDriveDecisionAsync(
        string roomId, string? capturedSprintId, RoundRunOutcome outcome, CancellationToken ct)
    {
        if (_selfDriveDecision is null) return;
        try
        {
            await _selfDriveDecision.DecideAsync(roomId, capturedSprintId, outcome, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Self-drive decision invocation failed for room {RoomId} sprint {SprintId}; idling",
                roomId, capturedSprintId);
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
