using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CANCEL_TASK — cancels a task in any non-terminal state.
/// Only Planner or Reviewer roles may cancel tasks.
/// Optionally deletes the associated task branch.
/// </summary>
public sealed class CancelTaskHandler : ICommandHandler
{
    private readonly IGitService _gitService;

    public CancelTaskHandler(IGitService gitService)
    {
        _gitService = gitService;
    }

    public string CommandName => "CANCEL_TASK";
    public bool IsDestructive => true;
    public string DestructiveWarning => "CANCEL_TASK will permanently cancel this task. The task branch may be deleted.";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Reviewer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only Planner, Reviewer, or Human roles can cancel tasks"
            };
        }

        if (!command.Args.TryGetValue("taskId", out var taskIdObj) || taskIdObj is not string taskId
            || string.IsNullOrWhiteSpace(taskId))
        {
            if (!command.Args.TryGetValue("value", out taskIdObj) || taskIdObj is not string taskIdValue
                || string.IsNullOrWhiteSpace(taskIdValue))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "Missing required argument: taskId"
                };
            }
            taskId = taskIdValue;
        }

        string? reason = null;
        if (command.Args.TryGetValue("reason", out var reasonObj) && reasonObj is string reasonStr
            && !string.IsNullOrWhiteSpace(reasonStr))
        {
            reason = reasonStr;
        }

        // Default: delete the branch. Pass deleteBranch: false to preserve it.
        bool deleteBranch = true;
        if (command.Args.TryGetValue("deleteBranch", out var delObj) && delObj is string delStr)
        {
            _ = bool.TryParse(delStr, out deleteBranch);
        }

        var messages = context.Services.GetRequiredService<IMessageService>();
        var taskQueries = context.Services.GetRequiredService<ITaskQueryService>();

        var task = await taskQueries.GetTaskAsync(taskId);
        if (task is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Task '{taskId}' not found"
            };
        }

        // Already in a terminal state
        if (task.Status is Shared.Models.TaskStatus.Completed or Shared.Models.TaskStatus.Cancelled)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Task '{task.Title}' is already {task.Status}"
            };
        }

        try
        {
            await taskQueries.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Cancelled);

            // Clean up the task branch if requested (default: yes)
            string? deletedBranch = null;
            if (deleteBranch && !string.IsNullOrWhiteSpace(task.BranchName))
            {
                try
                {
                    await _gitService.DeleteBranchAsync(task.BranchName);
                    deletedBranch = task.BranchName;
                }
                catch
                {
                    // Branch may already be deleted or never existed — not fatal
                }
            }

            if (!string.IsNullOrWhiteSpace(context.RoomId))
            {
                var reasonText = !string.IsNullOrWhiteSpace(reason) ? $" Reason: {reason}" : "";
                await messages.PostSystemStatusAsync(context.RoomId,
                    $"🗑️ Task \"{task.Title}\" cancelled by {context.AgentName}.{reasonText}");
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = task.Id,
                    ["title"] = task.Title,
                    ["previousStatus"] = task.Status.ToString(),
                    ["branchDeleted"] = deletedBranch,
                    ["message"] = $"Task '{task.Title}' cancelled"
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = ex.Message
            };
        }
    }
}
