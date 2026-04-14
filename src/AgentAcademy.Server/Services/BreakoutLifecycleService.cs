using System.Text.RegularExpressions;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages the breakout conversation loop: agent rounds, stuck detection, and
/// failure cleanup. Delegates completion/review to <see cref="BreakoutCompletionService"/>.
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
    private readonly IAgentCatalog _catalog;
    private readonly IAgentExecutor _executor;
    private readonly SpecManager _specManager;
    private readonly GitService _gitService;
    private readonly WorktreeService _worktreeService;
    private readonly AgentMemoryLoader _memoryLoader;
    private readonly BreakoutCompletionService _completion;
    private readonly ILogger<BreakoutLifecycleService> _logger;

    private volatile bool _stopped;

    public BreakoutLifecycleService(
        IServiceScopeFactory scopeFactory,
        IAgentCatalog catalog,
        IAgentExecutor executor,
        SpecManager specManager,
        GitService gitService,
        WorktreeService worktreeService,
        AgentMemoryLoader memoryLoader,
        BreakoutCompletionService completion,
        ILogger<BreakoutLifecycleService> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _executor = executor;
        _specManager = specManager;
        _gitService = gitService;
        _worktreeService = worktreeService;
        _memoryLoader = memoryLoader;
        _completion = completion;
        _logger = logger;
    }

    /// <summary>Signals the service to stop processing.</summary>
    public void Stop()
    {
        _stopped = true;
        _completion.Stopped = true;
    }

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
        var sessionQueryService = scope.ServiceProvider.GetRequiredService<ConversationSessionQueryService>();
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
                    var breakoutSummary = await sessionQueryService.GetSessionContextAsync(breakoutRoomId);

                    string? breakoutSpecContext = null;
                    var breakoutTaskId = await breakoutRoomService.GetBreakoutTaskIdAsync(breakoutRoomId);
                    if (breakoutTaskId is not null)
                    {
                        try
                        {
                            var specLinks = await taskQueryService.GetSpecLinksForTaskAsync(breakoutTaskId);
                            var linkedSectionIds = specLinks.Select(l => l.SpecSectionId);
                            var task = await taskQueryService.GetTaskAsync(breakoutTaskId);
                            var searchQuery = task is not null ? $"{task.Title} {task.Description}" : null;
                            breakoutSpecContext = await _specManager.LoadSpecContextWithRelevanceAsync(
                                searchQuery, linkedSectionIds);
                        }
                        catch { /* non-critical: fall through with null spec context */ }
                    }
                    breakoutSpecContext ??= await _specManager.LoadSpecContextAsync();

                    var breakoutVersionInfo = await _specManager.GetSpecVersionAsync();
                    var breakoutSpecVersion = breakoutVersionInfo?.Version;
                    var prompt = PromptBuilder.BuildBreakoutPrompt(agent, currentBr, round, breakoutMemories, breakoutDms, breakoutSummary, breakoutSpecContext, breakoutSpecVersion);
                    response = await _completion.RunAgentAsync(agent, prompt, breakoutRoomId, worktreePath);
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
                        var cmdResult = await _completion.ProcessCommandsAsync(agent, response, breakoutRoomId, worktreePath);
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
                await _completion.HandleBreakoutCompleteAsync(
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

        await _completion.HandleBreakoutCompleteAsync(
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
}
