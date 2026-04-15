using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Executes a single agent turn: resolves effective config, loads memories and DMs,
/// builds the prompt, runs the LLM, and processes the response (commands, message
/// posting, task assignments). Extracted from AgentOrchestrator to isolate the
/// per-turn execution concern from round-level orchestration.
/// </summary>
public sealed class AgentTurnRunner : IAgentTurnRunner
{
    private readonly IAgentExecutor _executor;
    private readonly CommandPipeline _commandPipeline;
    private readonly ITaskAssignmentHandler _taskAssignmentHandler;
    private readonly AgentMemoryLoader _memoryLoader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentTurnRunner> _logger;

    public AgentTurnRunner(
        IAgentExecutor executor,
        CommandPipeline commandPipeline,
        ITaskAssignmentHandler taskAssignmentHandler,
        AgentMemoryLoader memoryLoader,
        IServiceScopeFactory scopeFactory,
        ILogger<AgentTurnRunner> logger)
    {
        _executor = executor;
        _commandPipeline = commandPipeline;
        _taskAssignmentHandler = taskAssignmentHandler;
        _memoryLoader = memoryLoader;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Runs a single agent turn: resolves effective config, loads memories and DMs,
    /// builds the prompt, executes the agent, and processes the response (commands,
    /// message posting, task assignments). Returns the effective agent, raw response,
    /// and whether the response was substantive (non-pass/non-offline).
    /// </summary>
    public async Task<AgentTurnResult> RunAgentTurnAsync(
        AgentDefinition catalogAgent,
        IServiceScope scope,
        IMessageService messageService,
        IAgentConfigService configService,
        IActivityPublisher activity,
        RoomSnapshot room,
        string roomId,
        string? specContext,
        List<TaskItem>? taskItems = null,
        string? sessionSummary = null,
        string? sprintPreamble = null,
        string? promptSuffix = null,
        string? specVersion = null)
    {
        var agent = await configService.GetEffectiveAgentAsync(catalogAgent);

        await activity.PublishThinkingAsync(agent, roomId);
        string response;
        try
        {
            var memories = await _memoryLoader.LoadAsync(agent.Id);
            var dms = await messageService.GetDirectMessagesForAgentAsync(agent.Id);
            if (dms.Count > 0)
                await messageService.AcknowledgeDirectMessagesAsync(agent.Id, dms.Select(m => m.Id).ToList());

            var prompt = PromptBuilder.BuildConversationPrompt(
                agent, room, specContext, taskItems, memories, dms, sessionSummary, sprintPreamble, specVersion);
            if (promptSuffix is not null) prompt += promptSuffix;

            response = await RunAgentAsync(agent, prompt, roomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent {AgentName} failed", agent.Name);
            response = "";
        }
        finally
        {
            await activity.PublishFinishedAsync(agent, roomId);
        }

        bool isNonPass = !string.IsNullOrWhiteSpace(response)
            && !AgentResponseParser.IsPassResponse(response)
            && !AgentResponseParser.IsStubOfflineResponse(response);

        if (isNonPass)
        {
            await ProcessAndPostAgentResponseAsync(messageService, agent, roomId, response);
            foreach (var assignment in AgentResponseParser.ParseTaskAssignments(response))
                await _taskAssignmentHandler.ProcessAssignmentAsync(scope, agent, roomId, assignment);
        }

        return new AgentTurnResult(agent, response, isNonPass);
    }

    // ── AGENT EXECUTION ─────────────────────────────────────────

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

    // ── RESPONSE PROCESSING ─────────────────────────────────────

    /// <summary>
    /// Processes commands from an agent response, posts the remaining text,
    /// and posts command results as a system message for context visibility.
    /// </summary>
    private async Task ProcessAndPostAgentResponseAsync(
        IMessageService messageService, AgentDefinition agent, string roomId, string response)
    {
        var pipelineResult = await ProcessCommandsAsync(agent, response, roomId);

        var textToPost = pipelineResult.RemainingText;
        if (!string.IsNullOrWhiteSpace(textToPost) && !AgentResponseParser.IsPassResponse(textToPost))
        {
            await PostAgentMessageAsync(messageService, agent, roomId, textToPost);
        }

        var formattedResults = CommandPipeline.FormatResultsForContext(pipelineResult.Results);
        if (!string.IsNullOrEmpty(formattedResults))
        {
            await messageService.PostSystemStatusAsync(roomId, formattedResults);
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

    private async Task PostAgentMessageAsync(
        IMessageService messageService, AgentDefinition agent, string roomId, string content)
    {
        try
        {
            await messageService.PostMessageAsync(new PostMessageRequest(
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
