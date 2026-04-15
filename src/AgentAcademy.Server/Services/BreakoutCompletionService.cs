using System.Text.RegularExpressions;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles post-loop breakout completion: presenting results, running the review
/// cycle, handling review rejection (fix loop), and finalizing breakouts.
/// Also exposes agent execution helpers shared with <see cref="BreakoutLifecycleService"/>.
/// </summary>
public sealed class BreakoutCompletionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentExecutor _executor;
    private readonly SpecManager _specManager;
    private readonly CommandPipeline _commandPipeline;
    private readonly AgentMemoryLoader _memoryLoader;
    private readonly ILogger<BreakoutCompletionService> _logger;

    public BreakoutCompletionService(
        IServiceScopeFactory scopeFactory,
        IAgentCatalog catalog,
        IAgentExecutor executor,
        SpecManager specManager,
        CommandPipeline commandPipeline,
        AgentMemoryLoader memoryLoader,
        ILogger<BreakoutCompletionService> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _executor = executor;
        _specManager = specManager;
        _commandPipeline = commandPipeline;
        _memoryLoader = memoryLoader;
        _logger = logger;
    }

    /// <summary>Volatile flag shared with the lifecycle service to stop processing.</summary>
    internal volatile bool Stopped;

    // ── AGENT EXECUTION HELPERS ────────────────────────────────

    internal async Task<string> RunAgentAsync(
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

    internal async Task<CommandPipelineResult> ProcessCommandsAsync(
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

    // ── BREAKOUT COMPLETION / REVIEW ────────────────────────────

    internal async Task HandleBreakoutCompleteAsync(
        BreakoutRoomService breakoutRoomService, MessageService messageService,
        TaskItemService taskItemService, ITaskQueryService taskQueryService,
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
            var reviewVersionInfo = await _specManager.GetSpecVersionAsync();
            var prompt = PromptBuilder.BuildReviewPrompt(reviewer, presentingAgent.Name, workReport,
                await _specManager.LoadSpecContextAsync(), reviewVersionInfo?.Version);
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
        TaskItemService taskItemService, ITaskQueryService taskQueryService,
        AgentLocationService agentLocationService, RoomService roomService,
        string breakoutRoomId, string parentRoomId,
        AgentDefinition agent, BreakoutRoom br, string? worktreePath = null)
    {
        using var fixScope = _scopeFactory.CreateScope();
        var sessionService = fixScope.ServiceProvider.GetRequiredService<ConversationSessionQueryService>();

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
            if (Stopped) break;
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
                        var fixTask = await taskQueryService.GetTaskAsync(fixTaskId);
                        var searchQuery = fixTask is not null ? $"{fixTask.Title} {fixTask.Description}" : null;
                        fixSpecContext = await _specManager.LoadSpecContextWithRelevanceAsync(
                            searchQuery, fixSpecLinks.Select(l => l.SpecSectionId));
                    }
                    catch { /* non-critical */ }
                }
                fixSpecContext ??= await _specManager.LoadSpecContextAsync();

                var fixVersionInfo = await _specManager.GetSpecVersionAsync();
                response = await RunAgentAsync(
                    agent, PromptBuilder.BuildBreakoutPrompt(agent, updatedBr, round, fixMemories, fixDms, fixSummary, fixSpecContext, fixVersionInfo?.Version),
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

    private AgentDefinition? FindReviewer() =>
        _catalog.Agents.FirstOrDefault(a => a.Role == "Reviewer");
}
