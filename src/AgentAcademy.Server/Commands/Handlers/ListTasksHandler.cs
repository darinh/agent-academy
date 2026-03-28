using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_TASKS — returns tasks, optionally filtered by status and assignee.
/// </summary>
public sealed class ListTasksHandler : ICommandHandler
{
    public string CommandName => "LIST_TASKS";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();
        var tasks = await runtime.GetTasksAsync();

        // Apply optional filters from args
        if (command.Args.TryGetValue("status", out var statusObj) && statusObj is string statusFilter)
        {
            tasks = tasks.Where(t =>
                t.Status.ToString().Equals(statusFilter, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        if (command.Args.TryGetValue("assignee", out var assigneeObj) && assigneeObj is string assigneeFilter)
        {
            tasks = tasks.Where(t =>
                (t.AssignedAgentId?.Equals(assigneeFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.AssignedAgentName?.Equals(assigneeFilter, StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();
        }

        var result = tasks.Select(t => new Dictionary<string, object?>
        {
            ["id"] = t.Id,
            ["title"] = t.Title,
            ["status"] = t.Status.ToString(),
            ["assignedTo"] = t.AssignedAgentName ?? t.AssignedAgentId,
            ["description"] = t.Description,
            ["successCriteria"] = t.SuccessCriteria,
            ["branchName"] = t.BranchName,
            ["pullRequestUrl"] = t.PullRequestUrl,
            ["reviewRounds"] = t.ReviewRounds,
            ["createdAt"] = t.CreatedAt.ToString("o")
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?> { ["tasks"] = result, ["count"] = result.Count }
        };
    }
}
