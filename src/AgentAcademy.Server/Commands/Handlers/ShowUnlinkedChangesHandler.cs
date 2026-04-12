using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_UNLINKED_CHANGES — lists active tasks that have no spec links.
/// Useful for detecting spec drift and ensuring all work is traceable.
/// </summary>
public sealed class ShowUnlinkedChangesHandler : ICommandHandler
{
    public string CommandName => "SHOW_UNLINKED_CHANGES";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var taskQueries = context.Services.GetRequiredService<TaskQueryService>();

        var unlinkedTasks = await taskQueries.GetUnlinkedTasksAsync();

        if (unlinkedTasks.Count == 0)
        {
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["count"] = 0,
                    ["tasks"] = Array.Empty<object>(),
                    ["message"] = "All active tasks have spec links. No unlinked changes detected."
                }
            };
        }

        var taskSummaries = unlinkedTasks.Select(t => new Dictionary<string, object?>
        {
            ["taskId"] = t.Id,
            ["title"] = t.Title,
            ["status"] = t.Status.ToString(),
            ["assignee"] = t.AssignedAgentName ?? "(unassigned)",
            ["branch"] = t.BranchName
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["count"] = unlinkedTasks.Count,
                ["tasks"] = taskSummaries,
                ["message"] = $"{unlinkedTasks.Count} active task(s) have no spec links. " +
                    "Use LINK_TASK_TO_SPEC to associate them with spec sections."
            }
        };
    }
}
