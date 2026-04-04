using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_COMMANDS — returns all available commands with their names,
/// indicating which ones the requesting agent is authorized to use.
/// Resolves handlers at execution time to avoid circular DI dependency.
/// </summary>
public sealed class ListCommandsHandler : ICommandHandler
{
    public string CommandName => "LIST_COMMANDS";

    private static readonly Dictionary<string, string> CommandDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LIST_COMMANDS"] = "List all available commands with authorization status",
        ["LIST_ROOMS"] = "Show all rooms with status, phase, and participants",
        ["LIST_AGENTS"] = "Show all agents with location and state",
        ["LIST_TASKS"] = "Show tasks with optional status/assignee filters",
        ["READ_FILE"] = "Read file content (auto-truncates large files with continuation hints) or list directory entries",
        ["SEARCH_CODE"] = "Search codebase using git grep with optional path/glob/ignoreCase filters",
        ["REMEMBER"] = "Store a persistent memory (key/value with category)",
        ["RECALL"] = "Search stored memories by category or query",
        ["LIST_MEMORIES"] = "List all stored memories, optionally filtered by category",
        ["FORGET"] = "Delete a stored memory by key",
        ["SHOW_DIFF"] = "Show git diff (optionally against a branch)",
        ["GIT_LOG"] = "Show commit history with optional file/count/since filters",
        ["RUN_BUILD"] = "Run the project build and return output",
        ["RUN_TESTS"] = "Run the test suite (optional scope filter)",
        ["SHOW_REVIEW_QUEUE"] = "Show tasks awaiting review",
        ["ROOM_HISTORY"] = "Read messages from any room without moving",
        ["MOVE_TO_ROOM"] = "Move yourself to a different room",
        ["DM"] = "Send a private direct message to another agent or human",
        ["ASK_HUMAN"] = "Send a question to the human via notification provider",
        ["SHELL"] = "Execute an allowlisted shell operation",
        ["SET_PLAN"] = "Create or update the plan for a room",
        ["MERGE_TASK"] = "Squash-merge a completed task branch into develop",
        ["CANCEL_TASK"] = "Cancel a task and optionally delete its branch",
        ["APPROVE_TASK"] = "Approve a task after review",
        ["REQUEST_CHANGES"] = "Request changes on a task under review",
        ["CLAIM_TASK"] = "Claim an unassigned task",
        ["RELEASE_TASK"] = "Release a claimed task back to unassigned",
        ["UPDATE_TASK"] = "Update task fields (status, phase, plan, etc.)",
        ["ADD_TASK_COMMENT"] = "Attach a comment or finding to a task",
        ["CLOSE_ROOM"] = "Archive a non-main room",
        ["RESTART_SERVER"] = "Request a server restart via exit code 75",
    };

    public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Resolve handlers at execution time to avoid circular DI dependency
        var allHandlers = context.Services.GetServices<ICommandHandler>();
        var authorizer = new CommandAuthorizer();
        var agentDef = context.Services.GetRequiredService<WorkspaceRuntime>()
            .GetConfiguredAgents()
            .FirstOrDefault(a => a.Id == context.AgentId);

        var commands = allHandlers
            .Select(h =>
            {
                var authorized = true;
                if (agentDef is not null)
                {
                    var probe = new CommandEnvelope(
                        Command: h.CommandName,
                        Args: new Dictionary<string, object?>(),
                        Status: CommandStatus.Success,
                        Result: null,
                        Error: null,
                        CorrelationId: string.Empty,
                        Timestamp: DateTime.UtcNow,
                        ExecutedBy: context.AgentId ?? string.Empty
                    );
                    authorized = authorizer.Authorize(probe, agentDef) is null;
                }

                CommandDescriptions.TryGetValue(h.CommandName, out var description);

                return new Dictionary<string, object?>
                {
                    ["command"] = h.CommandName,
                    ["description"] = description ?? "(no description)",
                    ["authorized"] = authorized
                };
            })
            .OrderBy(c => c["command"]?.ToString())
            .ToList();

        return Task.FromResult(command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["commands"] = commands,
                ["count"] = commands.Count,
                ["authorizedCount"] = commands.Count(c => c["authorized"] is true)
            }
        });
    }
}
