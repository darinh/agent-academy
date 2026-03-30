using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles MERGE_TASK — squash-merges a task branch into develop.
/// Only Reviewer or Planner roles may invoke this command.
/// The task must be in Approved status and have a branch name set.
/// </summary>
public sealed class MergeTaskHandler : ICommandHandler
{
    private readonly GitService _gitService;

    public MergeTaskHandler(GitService gitService)
    {
        _gitService = gitService;
    }

    public string CommandName => "MERGE_TASK";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Parse taskId from args or value
        if (!command.Args.TryGetValue("taskId", out var taskIdObj) || taskIdObj is not string taskId
            || string.IsNullOrWhiteSpace(taskId))
        {
            if (!command.Args.TryGetValue("value", out taskIdObj) || taskIdObj is not string taskIdValue
                || string.IsNullOrWhiteSpace(taskIdValue))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    Error = "Missing required argument: TaskId"
                };
            }
            taskId = taskIdValue;
        }

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();

        // Validate task exists
        var task = await runtime.GetTaskAsync(taskId);
        if (task is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = $"Task '{taskId}' not found"
            };
        }

        // Validate task is Approved
        if (task.Status != Shared.Models.TaskStatus.Approved)
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = $"Task must be in Approved status to merge (current: {task.Status})"
            };
        }

        // Validate task has a branch
        if (string.IsNullOrWhiteSpace(task.BranchName))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = "Task has no branch to merge"
            };
        }

        try
        {
            // Set task to Merging status before git ops
            await runtime.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Merging);

            // Build commit message with metadata trailers
            var commitMessage = $"{task.Title}\n\nBranch: {task.BranchName}";
            if (!string.IsNullOrWhiteSpace(task.ReviewerAgentId))
                commitMessage += $"\nReviewed-by: {task.ReviewerAgentId}";
            commitMessage += $"\nCo-authored-by: {context.AgentName} <{context.AgentId}@agent-academy>";

            await _gitService.SquashMergeAsync(task.BranchName, commitMessage);
            await runtime.CompleteTaskAsync(taskId, commitCount: 1);

            // Post success note to task room
            if (!string.IsNullOrWhiteSpace(context.RoomId))
            {
                await runtime.PostSystemStatusAsync(context.RoomId,
                    $"✅ Task \"{task.Title}\" merged from {task.BranchName} into develop.");
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = task.Id,
                    ["title"] = task.Title,
                    ["branch"] = task.BranchName,
                    ["message"] = $"Task '{task.Title}' merged successfully"
                }
            };
        }
        catch (Exception ex)
        {
            // Broad catch for git/DB inconsistency recovery
            return command with
            {
                Status = CommandStatus.Error,
                Error = $"Merge failed: {ex.Message}"
            };
        }
    }
}
