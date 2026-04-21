using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_WORKTREES — returns all active worktrees with git status and linked task/agent info.
/// </summary>
public sealed class ListWorktreesHandler : ICommandHandler
{
    public string CommandName => "LIST_WORKTREES";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var worktreeService = context.Services.GetRequiredService<IWorktreeService>();
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();

        var worktrees = worktreeService.GetActiveWorktrees();
        if (worktrees.Count == 0)
        {
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["worktrees"] = Array.Empty<object>(),
                    ["count"] = 0,
                    ["message"] = "No active worktrees."
                }
            };
        }

        var branches = worktrees.Select(w => w.Branch).ToList();
        var tasksByBranch = await db.Tasks
            .Where(t => t.BranchName != null && branches.Contains(t.BranchName))
            .Select(t => new
            {
                t.BranchName,
                t.Id,
                t.Title,
                t.Status,
                t.AssignedAgentId,
                t.AssignedAgentName,
                t.UpdatedAt
            })
            .ToListAsync();

        var taskLookup = tasksByBranch
            .GroupBy(t => t.BranchName!)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(t => t.Status != "Completed" ? 1 : 0)
                      .ThenByDescending(t => t.UpdatedAt)
                      .First());

        var repoRoot = worktreeService.RepositoryRoot;

        // Optional status filter
        string? statusFilter = null;
        if (command.Args.TryGetValue("status", out var statusObj) && statusObj is string s)
            statusFilter = s;

        var result = new List<Dictionary<string, object?>>();
        foreach (var wt in worktrees)
        {
            var relativePath = Path.GetRelativePath(repoRoot, wt.Path);
            taskLookup.TryGetValue(wt.Branch, out var task);

            var taskStatus = task?.Status;

            // Apply filter if provided (matches on task status)
            if (statusFilter is not null
                && !(taskStatus?.Equals(statusFilter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            result.Add(new Dictionary<string, object?>
            {
                ["branch"] = wt.Branch,
                ["relativePath"] = relativePath,
                ["createdAt"] = wt.CreatedAt.ToString("o"),
                ["taskId"] = task?.Id,
                ["taskTitle"] = task?.Title,
                ["taskStatus"] = taskStatus,
                ["agentId"] = task?.AssignedAgentId,
                ["agentName"] = task?.AssignedAgentName
            });
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["worktrees"] = result,
                ["count"] = result.Count
            }
        };
    }
}
