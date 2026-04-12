using System.Text.RegularExpressions;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages the full lifecycle of an agent breakout session: the conversation
/// loop, stuck detection, completion/review flow, and cleanup. Extracted from
/// AgentOrchestrator to isolate the breakout concern.
/// </summary>
public sealed class BreakoutLifecycleService
{
    /// <summary>
    /// Consecutive breakout rounds with zero commands parsed before the agent is
    /// considered stuck and the breakout is terminated.
    /// </summary>
    internal const int MaxConsecutiveIdleRounds = 5;

    /// <summary>
    /// Absolute cap on breakout rounds. Prevents infinite loops regardless of
    /// whether the agent is issuing commands. Agents should complete or be
    /// recalled well before this limit.
    /// </summary>
    internal const int MaxBreakoutRounds = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentCatalogOptions _catalog;
    private readonly IAgentExecutor _executor;
    private readonly SpecManager _specManager;
    private readonly CommandPipeline _commandPipeline;
    private readonly GitService _gitService;
    private readonly WorktreeService _worktreeService;
    private readonly AgentMemoryLoader _memoryLoader;
    private readonly ILogger<BreakoutLifecycleService> _logger;

    private volatile bool _stopped;

    public BreakoutLifecycleService(
        IServiceScopeFactory scopeFactory,
        AgentCatalogOptions catalog,
        IAgentExecutor executor,
        SpecManager specManager,
        CommandPipeline commandPipeline,
        GitService gitService,
        WorktreeService worktreeService,
        AgentMemoryLoader memoryLoader,
        ILogger<BreakoutLifecycleService> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _executor = executor;
        _specManager = specManager;
        _commandPipeline = commandPipeline;
        _gitService = gitService;
        _worktreeService = worktreeService;
        _memoryLoader = memoryLoader;
        _logger = logger;
    }

    /// <summary>Signals the service to stop processing.</summary>
    public void Stop() => _stopped = true;

    /// <summary>
    /// Runs the full breakout lifecycle for an agent: conversation loop, completion,
    /// review cycle, and cleanup. This is the single entry point — it owns
    /// try/catch/finally so the caller doesn't need to handle failure cleanup.
    /// </summary>
    public async Task RunBreakoutLifecycleAsync(
        string breakoutRoomId, string agentId, string parentRoomId,
        AgentDefinition agent,
        string? taskBranch = null, string? worktreePath = null)
    {
        try
        {
            await RunBreakoutLoopAsync(breakoutRoomId, agentId, taskBranch, worktreePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Breakout loop failed for {AgentName} in {BreakoutId}", agent.Name, breakoutRoomId);
            await HandleBreakoutFailureAsync(breakoutRoomId, parentRoomId, agent, ex);
        }
        finally
        {
            if (worktreePath is not null && taskBranch is not null)
            {
                try { await _executor.DisposeWorktreeClientAsync(worktreePath); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispose worktree client for {Path}", worktreePath);
                }

                try
                {
                    await _worktreeService.RemoveWorktreeAsync(taskBranch);
                    _logger.LogInformation("Cleaned up worktree for {Branch} on breakout exit", taskBranch);
                }
                catch (Exception wtEx)
                {
                    _logger.LogWarning(wtEx, "Failed to clean up worktree for {Branch} on breakout exit", taskBranch);
                }
            }
        }
    }

    // ── BREAKOUT LOOP ───────────────────────────────────────────

    private async Task RunBreakoutLoopAsync(
        string breakoutRoomId, string agentId,
        string? taskBranch, string? worktreePath)
    {
        using var scope = _scopeFactory.CreateScope();
        var breakoutRoomService = scope.ServiceProvider.GetRequiredService<BreakoutRoomService>();
        var messageService = scope.ServiceProvider.GetRequiredService<MessageService>();
        var taskItemService = scope.ServiceProvider.GetRequiredService<TaskItemService>();
        var taskQueryService = scope.ServiceProvider.GetRequiredService<TaskQueryService>();
        var activity = scope.ServiceProvider.GetRequiredService<ActivityPublisher>();
        var configService = scope.ServiceProvider.GetRequiredService<AgentConfigService>();
        var sessionService = scope.ServiceProvider.GetRequiredService<ConversationSessionService>();
        var agentLocationService = scope.ServiceProvider.GetRequiredService<AgentLocationService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();

        var catalogAgent = _catalog.Agents.FirstOrDefault(a => a.Id == agentId);
        if (catalogAgent is null) return;
        var agent = await configService.GetEffectiveAgentAsync(catalogAgent);

        var br = await breakoutRoomService.GetBreakoutRoomAsync(breakoutRoomId);
        if (br is null) return;

        _logger.LogInformation("Starting breakout loop for {AgentName} in {BreakoutName}", agent.Name, br.Name);

        var tasks = await taskItemService.GetBreakoutTaskItemsAsync(breakoutRoomId);
        await messageService.PostBreakoutMessageAsync(
            breakoutRoomId, "system", "LocalAgentHost", "System",
            PromptBuilder.BuildTaskBrief(agent, tasks, taskBranch));

        var consecutiveIdleRounds = 0;

        for (var round = 1; ; round++)
        {
            if (_stopped) break;

            try
            {
                await sessionService.CheckAndRotateAsync(breakoutRoomId, "Breakout");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Session rotation check failed for breakout {BreakoutId}", breakoutRoomId);
            }

            var currentBr = await breakoutRoomService.GetBreakoutRoomAsync(breakoutRoomId);
            if (currentBr is null || currentBr.Status != RoomStatus.Active) break;

            if (round > MaxBreakoutRounds)
            {
                _logger.LogWarning(
                    "Agent {AgentName} hit max breakout rounds ({MaxRounds}) in {BreakoutId}",
                    agent.Name, MaxBreakoutRounds, breakoutRoomId);
                await HandleStuckDetectedAsync(
                    breakoutRoomService, messageService, taskQueryService,
                    breakoutRoomId, br.ParentRoomId, agent,
                    $"reached maximum round limit ({MaxBreakoutRounds})");
                return;
            }

            _logger.LogInformation("Breakout round {Round} for {AgentName}",
                round, agent.Name);

            var response = "";
            var isStubOffline = false;
            var commandCount = 0;
            var commandProcessingFailed = false;

            var useWorktree = worktreePath != null;
            if (!useWorktree && taskBranch != null)
                await _gitService.AcquireRoundLockAsync();
            try
            {
                if (!useWorktree && taskBranch != null)
                    await _gitService.EnsureBranchInternalAsync(taskBranch);

                try
                {
                    var breakoutMemories = await _memoryLoader.LoadAsync(agent.Id);
                    var breakoutDms = await messageService.GetDirectMessagesForAgentAsync(agent.Id);
                    if (breakoutDms.Count > 0)
                        await messageService.AcknowledgeDirectMessagesAsync(agent.Id, breakoutDms.Select(m => m.Id).ToList());
                    var breakoutSummary = await sessionService.GetSessionContextAsync(breakoutRoomId);

                    string? breakoutSpecContext = null;
                    var breakoutTaskId = await breakoutRoomService.GetBreakoutTaskIdAsync(breakoutRoomId);
                    if (breakoutTaskId is not null)
                    {
                        try
                        {
                            var specLinks = await taskQueryService.GetSpecLinksForTaskAsync(breakoutTaskId);
                            var linkedSectionIds = specLinks.Select(l => l.SpecSectionId);
                            breakoutSpecContext = await _specManager.LoadSpecContextForTaskAsync(linkedSectionIds);
                        }
                        catch { /* non-critical: fall through with null spec context */ }
                    }
                    breakoutSpecContext ??= await _specManager.LoadSpecContextAsync();

                    var prompt = PromptBuilder.BuildBreakoutPrompt(agent, currentBr, round, breakoutMemories, breakoutDms, breakoutSummary, breakoutSpecContext);
                    response = await RunAgentAsync(agent, prompt, breakoutRoomId, worktreePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Breakout agent {AgentName} failed in round {Round}", agent.Name, round);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(response))
                {
                    isStubOffline = AgentResponseParser.IsStubOfflineResponse(response);
                    if (!isStubOffline)
                    {
                        var cmdResult = await ProcessCommandsAsync(agent, response, breakoutRoomId, worktreePath);
                        commandCount = cmdResult.Results.Count;
                        commandProcessingFailed = cmdResult.ProcessingFailed;
                    }
                }
            }
            finally
            {
                if (!useWorktree && taskBranch != null)
                {
                    await _gitService.ReturnToDevelopInternalAsync(taskBranch);
                    _gitService.ReleaseRoundLock();
                }
            }

            if (isStubOffline)
            {
                _logger.LogWarning(
                    "Agent {AgentName} returned stub offline notice in breakout — aborting loop",
                    agent.Name);
                await messageService.PostBreakoutMessageAsync(
                    breakoutRoomId, "system", "LocalAgentHost", "System",
                    $"⚠️ {agent.Name} is offline. Breakout suspended until the Copilot SDK is reconnected.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(response))
            {
                await messageService.PostBreakoutMessageAsync(
                    breakoutRoomId, agent.Id, agent.Name, agent.Role, response);
            }

            var report = AgentResponseParser.ParseWorkReport(response);
            if (report is not null && Regex.IsMatch(report.Status, @"complete", RegexOptions.IgnoreCase))
            {
                var latestTasks = await taskItemService.GetBreakoutTaskItemsAsync(breakoutRoomId);
                foreach (var task in latestTasks)
                {
                    try { await taskItemService.UpdateTaskItemStatusAsync(task.Id, TaskItemStatus.Done, report.Evidence); }
                    catch { /* ok */ }
                }
                await HandleBreakoutCompleteAsync(
                    breakoutRoomService, messageService, taskItemService, taskQueryService,
                    agentLocationService, roomService, activity, configService,
                    breakoutRoomId, br.ParentRoomId, worktreePath);
                return;
            }

            if (!commandProcessingFailed)
            {
                if (commandCount > 0)
                    consecutiveIdleRounds = 0;
                else
                    consecutiveIdleRounds++;
            }

            if (consecutiveIdleRounds >= MaxConsecutiveIdleRounds)
            {
                _logger.LogWarning(
                    "Agent {AgentName} stuck in {BreakoutId}: {IdleRounds} consecutive rounds with no commands",
                    agent.Name, breakoutRoomId, consecutiveIdleRounds);
                await HandleStuckDetectedAsync(
                    breakoutRoomService, messageService, taskQueryService,
                    breakoutRoomId, br.ParentRoomId, agent,
                    $"no commands for {MaxConsecutiveIdleRounds} consecutive rounds");
                return;
            }
        }

        _logger.LogInformation("Breakout loop ended for {AgentName}", agent.Name);

        var finalBr = await breakoutRoomService.GetBreakoutRoomAsync(breakoutRoomId);
        if (finalBr is null || finalBr.Status != RoomStatus.Active)
        {
            _logger.LogInformation("Breakout {BreakoutId} was recalled or archived — skipping completion flow", breakoutRoomId);
            return;
        }

        await HandleBreakoutCompleteAsync(
            breakoutRoomService, messageService, taskItemService, taskQueryService,
            agentLocationService, roomService, activity, configService,
            breakoutRoomId, br.ParentRoomId, worktreePath);
    }

    // ── STUCK DETECTION ────────────────────────────────────────

    private async Task HandleStuckDetectedAsync(
        BreakoutRoomService breakoutRoomService,
        MessageService messageService,
        TaskQueryService taskQueryService,
        string breakoutRoomId, string parentRoomId,
        AgentDefinition agent, string reason)
    {
        var breakoutName = (await breakoutRoomService.GetBreakoutRoomAsync(breakoutRoomId))?.Name ?? breakoutRoomId;

        await messageService.PostBreakoutMessageAsync(
            breakoutRoomId, "system", "LocalAgentHost", "System",
            $"🔴 Stuck detection triggered for {agent.Name}: {reason}. Closing breakout.");

        var taskBlocked = false;
        var taskId = await breakoutRoomService.GetBreakoutTaskIdAsync(breakoutRoomId);
        if (taskId is not null)
        {
            try
            {
                await taskQueryService.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Blocked);
                taskBlocked = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to mark task {TaskId} as Blocked during stuck detection", taskId);
            }
        }

        await breakoutRoomService.CloseBreakoutRoomAsync(breakoutRoomId, BreakoutRoomCloseReason.StuckDetected);

        var taskNote = taskBlocked ? "Task marked as blocked." : taskId is not null
            ? "Failed to update task status." : "No linked task.";
        await messageService.PostSystemStatusAsync(parentRoomId,
            $"🔴 {agent.Name} was stuck in breakout \"{breakoutName}\": {reason}. " +
            $"Breakout closed. {taskNote} Agent returned to idle.");
    }

    // ── BREAKOUT FAILURE ───────────────────────────────────────

    private async Task HandleBreakoutFailureAsync(
        string breakoutRoomId, string parentRoomId,
        AgentDefinition agent, Exception ex)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var breakoutRoomService = scope.ServiceProvider.GetRequiredService<BreakoutRoomService>();
            var messageService = scope.ServiceProvider.GetRequiredService<MessageService>();
            var taskQueryService = scope.ServiceProvider.GetRequiredService<TaskQueryService>();

            var breakoutName = (await breakoutRoomService.GetBreakoutRoomAsync(breakoutRoomId))?.Name ?? breakoutRoomId;

            // Mark the linked task as Blocked (worktree cleanup is handled by the
            // caller's finally block — this method focuses on room/task state only)
            var taskId = await breakoutRoomService.GetBreakoutTaskIdAsync(breakoutRoomId);
            if (taskId is not null)
            {
                try
                {
                    await taskQueryService.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Blocked);
                }
                catch (Exception taskEx)
                {
                    _logger.LogWarning(taskEx,
                        "Failed to mark task {TaskId} as Blocked during breakout failure cleanup", taskId);
                }
            }

            await breakoutRoomService.CloseBreakoutRoomAsync(breakoutRoomId, BreakoutRoomCloseReason.Failed);

            await messageService.PostSystemStatusAsync(parentRoomId,
                $"🔴 {agent.Name} encountered an error in breakout \"{breakoutName}\": {ex.Message}. " +
                $"Breakout closed. Agent returned to idle.");
        }
        catch (Exception cleanupEx)
        {
            _logger.LogError(cleanupEx,
                "Failed to clean up after breakout failure for {AgentName} in {BreakoutId}",
                agent.Name, breakoutRoomId);
        }
    }

    // ── BREAKOUT COMPLETION / REVIEW ────────────────────────────

    private async Task HandleBreakoutCompleteAsync(
        BreakoutRoomService breakoutRoomService, MessageService messageService,
        TaskItemService taskItemService, TaskQueryService taskQueryService,
        AgentLocationService agentLocationService, RoomService roomService,
        ActivityPublisher activity, AgentConfigService configService,
        string breakoutRoomId, string parentRoomId, string? worktreePath = null)
    {
        var br = await breakoutRoomService.GetBreakoutRoomAsync(breakoutRoomId);
        if (br is null) return;

        var catalogAgent = _catalog.Agents.FirstOrDefault(a => a.Id == br.AssignedAgentId);
        if (catalogAgent is null) return;
        var agent = await configService.GetEffectiveAgentAsync(catalogAgent);

        _logger.LogInformation("Breakout complete: {AgentName} returning from {BreakoutName}", agent.Name, br.Name);

        await agentLocationService.MoveAgentAsync(agent.Id, parentRoomId, AgentState.Presenting);
        await messageService.PostSystemStatusAsync(parentRoomId,
            $"🎯 {agent.Name} has completed work in \"{br.Name}\" and is presenting results.");

        var agentMessages = br.RecentMessages.Where(m => m.SenderId == agent.Id).ToList();
        var lastMessage = agentMessages.LastOrDefault();
        if (lastMessage is not null)
        {
            try
            {
                await messageService.PostMessageAsync(new PostMessageRequest(
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

        var reviewTask = await breakoutRoomService.TransitionBreakoutTaskToInReviewAsync(breakoutRoomId);

        if (reviewTask?.BranchName is not null)
        {
            await messageService.PostSystemStatusAsync(parentRoomId,
                $"📋 Task \"{reviewTask.Title}\" is now **InReview** on branch `{reviewTask.BranchName}`. " +
                $"Use APPROVE_TASK to approve, then MERGE_TASK to merge into develop.");
            await FinalizeBreakoutAsync(breakoutRoomService, breakoutRoomId);
        }
        else
        {
            var verdict = await RunReviewCycleAsync(
                breakoutRoomService, messageService, roomService,
                activity, configService, parentRoomId, agent, lastMessage?.Content ?? "");

            var isApproved = verdict is null ||
                Regex.IsMatch(verdict.Verdict, @"^\s*APPROVED", RegexOptions.IgnoreCase);

            if (!isApproved)
            {
                await HandleReviewRejectionAsync(
                    breakoutRoomService, messageService, taskItemService, taskQueryService,
                    agentLocationService, roomService,
                    breakoutRoomId, parentRoomId, agent, br, worktreePath);
            }
            else
            {
                await FinalizeBreakoutAsync(breakoutRoomService, breakoutRoomId);
            }
        }
    }

    private async Task<ParsedReviewVerdict?> RunReviewCycleAsync(
        BreakoutRoomService breakoutRoomService, MessageService messageService,
        RoomService roomService,
        ActivityPublisher activity, AgentConfigService configService,
        string parentRoomId, AgentDefinition presentingAgent, string workReport)
    {
        var catalogReviewer = FindReviewer();
        if (catalogReviewer is null) return null;
        var reviewer = await configService.GetEffectiveAgentAsync(catalogReviewer);

        await activity.PublishThinkingAsync(reviewer, parentRoomId);
        var reviewResponse = "";
        try
        {
            var room = await roomService.GetRoomAsync(parentRoomId);
            if (room is null) return null;
            var prompt = PromptBuilder.BuildReviewPrompt(reviewer, presentingAgent.Name, workReport,
                await _specManager.LoadSpecContextAsync());
            reviewResponse = await RunAgentAsync(reviewer, prompt, parentRoomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reviewer failed");
            return null;
        }
        finally
        {
            await activity.PublishFinishedAsync(reviewer, parentRoomId);
        }

        if (string.IsNullOrWhiteSpace(reviewResponse) || AgentResponseParser.IsPassResponse(reviewResponse))
            return null;

        if (AgentResponseParser.IsStubOfflineResponse(reviewResponse))
        {
            _logger.LogWarning("Reviewer returned stub offline notice — skipping review cycle");
            return null;
        }

        try
        {
            await messageService.PostMessageAsync(new PostMessageRequest(
                RoomId: parentRoomId,
                SenderId: reviewer.Id,
                Content: reviewResponse,
                Kind: MessageKind.Review));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post review");
        }

        return AgentResponseParser.ParseReviewVerdict(reviewResponse);
    }

    private async Task HandleReviewRejectionAsync(
        BreakoutRoomService breakoutRoomService, MessageService messageService,
        TaskItemService taskItemService, TaskQueryService taskQueryService,
        AgentLocationService agentLocationService, RoomService roomService,
        string breakoutRoomId, string parentRoomId,
        AgentDefinition agent, BreakoutRoom br, string? worktreePath = null)
    {
        using var fixScope = _scopeFactory.CreateScope();
        var sessionService = fixScope.ServiceProvider.GetRequiredService<ConversationSessionService>();

        await messageService.PostSystemStatusAsync(parentRoomId,
            $"🔄 {agent.Name} is returning to \"{br.Name}\" to address review feedback.");
        await agentLocationService.MoveAgentAsync(agent.Id, parentRoomId, AgentState.Working, breakoutRoomId);

        var room = await roomService.GetRoomAsync(parentRoomId);
        var reviewMessage = room?.RecentMessages
            .Where(m => m.Kind == MessageKind.Review)
            .LastOrDefault();

        if (reviewMessage is not null)
        {
            await messageService.PostBreakoutMessageAsync(
                breakoutRoomId, "system", "LocalAgentHost", "System",
                $"Review feedback:\n{reviewMessage.Content}\n\nPlease address the findings and produce an updated WORK REPORT.");
        }

        for (var round = 1; ; round++)
        {
            if (_stopped) break;
            var updatedBr = await breakoutRoomService.GetBreakoutRoomAsync(breakoutRoomId);
            if (updatedBr is null || updatedBr.Status != RoomStatus.Active) break;

            var response = "";
            try
            {
                var fixMemories = await _memoryLoader.LoadAsync(agent.Id);
                var fixDms = await messageService.GetDirectMessagesForAgentAsync(agent.Id);
                if (fixDms.Count > 0)
                    await messageService.AcknowledgeDirectMessagesAsync(agent.Id, fixDms.Select(m => m.Id).ToList());
                var fixSummary = await sessionService.GetSessionContextAsync(breakoutRoomId);

                string? fixSpecContext = null;
                var fixTaskId = await breakoutRoomService.GetBreakoutTaskIdAsync(breakoutRoomId);
                if (fixTaskId is not null)
                {
                    try
                    {
                        var fixSpecLinks = await taskQueryService.GetSpecLinksForTaskAsync(fixTaskId);
                        fixSpecContext = await _specManager.LoadSpecContextForTaskAsync(
                            fixSpecLinks.Select(l => l.SpecSectionId));
                    }
                    catch { /* non-critical */ }
                }
                fixSpecContext ??= await _specManager.LoadSpecContextAsync();

                response = await RunAgentAsync(
                    agent, PromptBuilder.BuildBreakoutPrompt(agent, updatedBr, round, fixMemories, fixDms, fixSummary, fixSpecContext),
                    breakoutRoomId);
            }
            catch { continue; }

            if (!string.IsNullOrWhiteSpace(response))
            {
                if (AgentResponseParser.IsStubOfflineResponse(response))
                {
                    _logger.LogWarning(
                        "Agent {AgentName} returned stub offline notice in fix round — aborting",
                        agent.Name);
                    return;
                }

                await messageService.PostBreakoutMessageAsync(
                    breakoutRoomId, agent.Id, agent.Name, agent.Role, response);

                await ProcessCommandsAsync(agent, response, breakoutRoomId, worktreePath);
            }

            var report = AgentResponseParser.ParseWorkReport(response);
            if (report is not null && Regex.IsMatch(report.Status, @"complete", RegexOptions.IgnoreCase))
            {
                var latestTasks = await taskItemService.GetBreakoutTaskItemsAsync(breakoutRoomId);
                foreach (var task in latestTasks)
                {
                    try { await taskItemService.UpdateTaskItemStatusAsync(task.Id, TaskItemStatus.Done, report.Evidence); }
                    catch { /* ok */ }
                }
                break;
            }
        }

        await FinalizeBreakoutAsync(breakoutRoomService, breakoutRoomId);
    }

    private async Task FinalizeBreakoutAsync(BreakoutRoomService breakoutRoomService, string breakoutRoomId)
    {
        try
        {
            await breakoutRoomService.CloseBreakoutRoomAsync(breakoutRoomId, BreakoutRoomCloseReason.Completed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close breakout room {BreakoutId}", breakoutRoomId);
        }
    }

    // ── HELPERS ─────────────────────────────────────────────────

    private AgentDefinition? FindReviewer() =>
        _catalog.Agents.FirstOrDefault(a => a.Role == "Reviewer");

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
}
