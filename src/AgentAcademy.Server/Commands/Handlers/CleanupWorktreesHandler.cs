using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CLEANUP_WORKTREES — removes worktrees whose linked tasks are completed or cancelled.
/// Only planners and humans can trigger cleanup.
/// </summary>
public sealed class CleanupWorktreesHandler : ICommandHandler
{
    public string CommandName => "CLEANUP_WORKTREES";
    public bool IsDestructive => true;
    public string DestructiveWarning =>
        "CLEANUP_WORKTREES will remove all worktrees whose linked tasks are completed or cancelled. " +
        "Uncommitted changes in those worktrees will be lost.";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only planners and humans can cleanup worktrees."
            };
        }

        var worktreeService = context.Services.GetRequiredService<WorktreeService>();
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();

        var worktrees = worktreeService.GetActiveWorktrees();
        if (worktrees.Count == 0)
        {
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["removedCount"] = 0,
                    ["message"] = "No active worktrees to clean up."
                }
            };
        }

        var branches = worktrees.Select(w => w.Branch).ToList();

        // Find branches linked to completed or cancelled tasks
        var completedBranches = await db.Tasks
            .Where(t => t.BranchName != null
                && branches.Contains(t.BranchName)
                && (t.Status == "Completed" || t.Status == "Cancelled"))
            .Select(t => t.BranchName!)
            .Distinct()
            .ToListAsync();

        // Also include orphan worktrees (no linked task) if "includeOrphans" arg is set
        var includeOrphans = command.Args.TryGetValue("includeOrphans", out var orphanObj)
            && (orphanObj is true || (orphanObj is string orphanStr
                && orphanStr.Equals("true", StringComparison.OrdinalIgnoreCase)));

        var branchesWithTasks = await db.Tasks
            .Where(t => t.BranchName != null && branches.Contains(t.BranchName))
            .Select(t => t.BranchName!)
            .Distinct()
            .ToListAsync();

        var orphanBranches = includeOrphans
            ? branches.Except(branchesWithTasks).ToList()
            : new List<string>();

        var toRemove = completedBranches.Union(orphanBranches).ToList();

        var removed = new List<string>();
        foreach (var branch in toRemove)
        {
            await worktreeService.RemoveWorktreeAsync(branch);
            removed.Add(branch);
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["removedCount"] = removed.Count,
                ["removedBranches"] = removed,
                ["message"] = removed.Count > 0
                    ? $"Removed {removed.Count} stale worktree(s): {string.Join(", ", removed)}."
                    : "No stale worktrees found to clean up."
            }
        };
    }
}
