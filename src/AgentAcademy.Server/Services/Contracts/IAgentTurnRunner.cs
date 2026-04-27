using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Executes a single agent turn: resolves effective config, loads memories and DMs,
/// builds the prompt, runs the LLM, and processes the response (commands, message
/// posting, task assignments).
/// </summary>
public interface IAgentTurnRunner
{
    /// <summary>
    /// Runs a single agent turn: resolves effective config, loads memories and DMs,
    /// builds the prompt, executes the agent, and processes the response (commands,
    /// message posting, task assignments). Returns the effective agent, raw response,
    /// and whether the response was substantive (non-pass/non-offline).
    /// </summary>
    Task<AgentTurnResult> RunAgentTurnAsync(
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
        string? specVersion = null,
        // Optional sprint id for watchdog diagnostics. ConversationRoundRunner
        // captures this once per round (TOCTOU-safe) and threads it through.
        string? sprintId = null,
        // Optional workspace path that scopes file-touching tools (write_file,
        // read_file, search_code, commit_changes) and structured-command
        // handlers to a specific checkout/worktree. ConversationRoundRunner
        // resolves this from the room's WorkspacePath; breakouts get scoped
        // through a different path (BreakoutCompletionService.RunAgentAsync)
        // and pass null here. When null, tools fall back to the develop
        // checkout via FindProjectRoot() — appropriate for main-room work.
        // P1.9 blocker B (upstream wiring): without this thread, conversation
        // rounds always run with workspacePath=null in the executor, so any
        // file-touching tool resolves to the develop checkout regardless of
        // which room/workspace the agent is "in".
        string? workspacePath = null,
        // Cooperative cancellation. Forwarded into the executor and the
        // per-turn AgentLivenessTracker entry. The watchdog cancels through
        // this token when it detects a silent stall.
        CancellationToken cancellationToken = default);
}
