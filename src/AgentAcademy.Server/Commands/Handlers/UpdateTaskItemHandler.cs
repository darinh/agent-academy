using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles UPDATE_TASK_ITEM — updates the status and optional evidence of a task item.
/// The calling agent must be the item's assignee, a Planner, or a Reviewer.
/// </summary>
public sealed class UpdateTaskItemHandler : ICommandHandler
{
    public string CommandName => "UPDATE_TASK_ITEM";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("taskItemId", out var idObj) || idObj is not string taskItemId
            || string.IsNullOrWhiteSpace(taskItemId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: taskItemId"
            };
        }

        if (!command.Args.TryGetValue("status", out var statusObj) || statusObj is not string statusStr
            || string.IsNullOrWhiteSpace(statusStr))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: status (Pending, Active, Done, Rejected)"
            };
        }

        if (!Enum.TryParse<TaskItemStatus>(statusStr, ignoreCase: true, out var status)
            || !Enum.IsDefined(status))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = $"Invalid status: '{statusStr}'. Valid values: Pending, Active, Done, Rejected"
            };
        }

        string? evidence = command.Args.TryGetValue("evidence", out var evObj)
            && evObj is string ev && !string.IsNullOrWhiteSpace(ev) ? ev : null;

        var taskItems = context.Services.GetRequiredService<ITaskItemService>();

        var item = await taskItems.GetTaskItemAsync(taskItemId);
        if (item is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Task item '{taskItemId}' not found"
            };
        }

        var isAssignee = string.Equals(item.AssignedTo, context.AgentId, StringComparison.OrdinalIgnoreCase);
        var isPlanner = string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase);
        var isReviewer = string.Equals(context.AgentRole, "Reviewer", StringComparison.OrdinalIgnoreCase);
        var isHuman = string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase);

        if (!isAssignee && !isPlanner && !isReviewer && !isHuman)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Permission,
                Error = $"Only the assignee, Planner, Reviewer, or Human can update task item '{taskItemId}'"
            };
        }

        try
        {
            await taskItems.UpdateTaskItemStatusAsync(taskItemId, status, evidence);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskItemId"] = taskItemId,
                    ["title"] = item.Title,
                    ["status"] = status.ToString(),
                    ["evidence"] = evidence,
                    ["message"] = $"Task item '{item.Title}' updated to {status}"
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Infer(ex.Message),
                Error = ex.Message
            };
        }
    }
}
