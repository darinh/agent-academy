using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles MARK_BLOCKED — transitions a task to Blocked status and records
/// a Blocker-typed comment with the given reason.
/// </summary>
public sealed class MarkBlockedHandler : ICommandHandler
{
    public string CommandName => "MARK_BLOCKED";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!TryGetTaskId(command, out var taskId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: taskId"
            };
        }

        if (!command.Args.TryGetValue("reason", out var reasonObj) || reasonObj is not string reason
            || string.IsNullOrWhiteSpace(reason))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: reason"
            };
        }

        var taskQueries = context.Services.GetRequiredService<ITaskQueryService>();
        var taskLifecycle = context.Services.GetRequiredService<ITaskLifecycleService>();
        var messages = context.Services.GetRequiredService<IMessageService>();

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

        if (task.Status is Shared.Models.TaskStatus.Completed or Shared.Models.TaskStatus.Cancelled)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Cannot block task '{task.Title}' — it is already {task.Status}"
            };
        }

        if (task.Status is Shared.Models.TaskStatus.Blocked)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Task '{task.Title}' is already Blocked"
            };
        }

        if (task.Status is Shared.Models.TaskStatus.Approved or Shared.Models.TaskStatus.Merging)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Cannot block task '{task.Title}' — it is in {task.Status} state (merge workflow in progress)"
            };
        }

        try
        {
            var previousStatus = task.Status;
            await taskQueries.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Blocked);

            // Record blocker comment — non-critical, don't fail the block if this fails
            try
            {
                await taskLifecycle.AddTaskCommentAsync(
                    taskId, context.AgentId, context.AgentName,
                    TaskCommentType.Blocker, reason);
            }
            catch
            {
                // Comment recording failed but status transition succeeded
            }

            if (!string.IsNullOrWhiteSpace(context.RoomId))
            {
                try
                {
                    await messages.PostSystemStatusAsync(context.RoomId,
                        $"🚫 Task \"{task.Title}\" blocked by {context.AgentName}: {reason}");
                }
                catch
                {
                    // Room notification is non-critical — don't fail the block operation
                }
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = task.Id,
                    ["title"] = task.Title,
                    ["previousStatus"] = previousStatus.ToString(),
                    ["status"] = "Blocked",
                    ["reason"] = reason,
                    ["message"] = $"Task '{task.Title}' marked as Blocked: {reason}"
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

    private static bool TryGetTaskId(CommandEnvelope command, out string taskId)
    {
        if (command.Args.TryGetValue("taskId", out var obj) && obj is string id && !string.IsNullOrWhiteSpace(id))
        {
            taskId = id;
            return true;
        }
        if (command.Args.TryGetValue("value", out obj) && obj is string val && !string.IsNullOrWhiteSpace(val))
        {
            taskId = val;
            return true;
        }
        taskId = "";
        return false;
    }
}
