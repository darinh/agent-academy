using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles REBASE_TASK — rebases a task's feature branch onto develop.
/// Any role with task access may invoke this command to bring a branch up to date.
/// Returns conflict details when the rebase cannot be completed automatically.
/// </summary>
public sealed class RebaseTaskHandler : ICommandHandler
{
    private readonly GitService _gitService;

    public RebaseTaskHandler(GitService gitService)
    {
        _gitService = gitService;
    }

    public string CommandName => "REBASE_TASK";

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
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "Missing required argument: taskId"
                };
            }
            taskId = taskIdValue;
        }

        var messages = context.Services.GetRequiredService<MessageService>();
        var taskQueries = context.Services.GetRequiredService<ITaskQueryService>();

        // Validate task exists
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

        // Role gate: assigned agent, Planner, Reviewer, or Human
        var isAssigned = string.Equals(context.AgentId, task.AssignedAgentId, StringComparison.OrdinalIgnoreCase);
        var isPrivileged = context.AgentRole is "Planner" or "Reviewer" or "Human";
        if (!isAssigned && !isPrivileged)
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only the assigned agent, Planner, Reviewer, or Human roles can rebase a task branch"
            };
        }

        // Validate task has a branch
        if (string.IsNullOrWhiteSpace(task.BranchName))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = "Task has no branch to rebase"
            };
        }

        // Reject rebase for terminal tasks
        if (task.Status is Shared.Models.TaskStatus.Completed or Shared.Models.TaskStatus.Cancelled)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Cannot rebase a {task.Status} task"
            };
        }

        // Optional: check for conflicts first without modifying anything
        var dryRun = command.Args.TryGetValue("dryRun", out var dryRunObj)
            && dryRunObj is string dryRunStr
            && string.Equals(dryRunStr, "true", StringComparison.OrdinalIgnoreCase);

        if (dryRun)
        {
            return await HandleDryRunAsync(command, task);
        }

        return await HandleRebaseAsync(command, task, context.RoomId, messages);
    }

    private async Task<CommandEnvelope> HandleDryRunAsync(
        CommandEnvelope command, TaskSnapshot task)
    {
        try
        {
            var result = await _gitService.DetectMergeConflictsAsync(task.BranchName!);

            if (result.HasConflicts)
            {
                return command with
                {
                    Status = CommandStatus.Success,
                    Result = new Dictionary<string, object?>
                    {
                        ["taskId"] = task.Id,
                        ["branch"] = task.BranchName,
                        ["hasConflicts"] = true,
                        ["conflictingFiles"] = result.ConflictingFiles,
                        ["message"] = $"Conflict detected: {string.Join(", ", result.ConflictingFiles)}. "
                            + "Resolve conflicts on the feature branch before merging."
                    }
                };
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = task.Id,
                    ["branch"] = task.BranchName,
                    ["hasConflicts"] = false,
                    ["message"] = $"No conflicts detected — branch '{task.BranchName}' is ready to merge."
                }
            };
        }
        catch (Exception ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Conflict check failed: {ex.Message}"
            };
        }
    }

    private async Task<CommandEnvelope> HandleRebaseAsync(
        CommandEnvelope command, TaskSnapshot task, string? roomId, MessageService messages)
    {
        try
        {
            var newHead = await _gitService.RebaseAsync(task.BranchName!);

            // Post success note to task room
            if (!string.IsNullOrWhiteSpace(roomId))
            {
                await messages.PostSystemStatusAsync(roomId,
                    $"🔄 Branch \"{task.BranchName}\" rebased onto develop (HEAD: {newHead[..Math.Min(7, newHead.Length)]}).");
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = task.Id,
                    ["branch"] = task.BranchName,
                    ["newHead"] = newHead,
                    ["message"] = $"Branch '{task.BranchName}' rebased onto develop successfully"
                }
            };
        }
        catch (MergeConflictException ex)
        {
            // Post conflict note to task room
            if (!string.IsNullOrWhiteSpace(roomId))
            {
                await messages.PostSystemStatusAsync(roomId,
                    $"⚠️ Rebase conflict on \"{task.BranchName}\": {string.Join(", ", ex.ConflictingFiles)}. "
                    + "Manual conflict resolution needed.");
            }

            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Rebase conflict on '{task.BranchName}'. Conflicting files: "
                    + string.Join(", ", ex.ConflictingFiles)
                    + ". Resolve conflicts manually on the feature branch, then retry."
            };
        }
        catch (Exception ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Rebase failed: {ex.Message}"
            };
        }
    }
}
