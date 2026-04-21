using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles REQUEST_REVIEW — submits a task for review by transitioning it to InReview,
/// validating the task is in a reviewable state, and posting a notification.
/// Accepts tasks in Active, AwaitingValidation, or ChangesRequested states.
/// </summary>
public sealed class RequestReviewHandler : ICommandHandler
{
    public string CommandName => "REQUEST_REVIEW";

    private static readonly HashSet<TaskStatus> ReviewableStates = new()
    {
        TaskStatus.Active,
        TaskStatus.AwaitingValidation,
        TaskStatus.ChangesRequested,
    };

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

        var taskQueries = context.Services.GetRequiredService<ITaskQueryService>();
        var taskOrchestration = context.Services.GetRequiredService<ITaskOrchestrationService>();

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

        // Validate caller is the assignee, Planner, or Human
        var isAssignee = string.Equals(task.AssignedAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase);
        var isPlanner = string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase);
        var isHuman = string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase);

        if (!isAssignee && !isPlanner && !isHuman)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Permission,
                Error = $"Only the assignee ({task.AssignedAgentName ?? "unassigned"}), Planner, or Human can request review"
            };
        }

        // Validate task is in a reviewable state
        if (!ReviewableStates.Contains(task.Status))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Task '{task.Title}' is in {task.Status} state — must be Active, AwaitingValidation, or ChangesRequested to request review"
            };
        }

        // Already InReview is a no-op success
        if (task.Status == TaskStatus.InReview)
        {
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = task.Id,
                    ["title"] = task.Title,
                    ["status"] = task.Status.ToString(),
                    ["message"] = $"Task '{task.Title}' is already InReview"
                }
            };
        }

        try
        {
            // Transition to InReview
            var updated = await taskQueries.UpdateTaskStatusAsync(taskId, TaskStatus.InReview);

            // Post summary as a task comment if provided
            var hasSummary = command.Args.TryGetValue("summary", out var summaryObj)
                && summaryObj is string summary && !string.IsNullOrWhiteSpace(summary);

            if (hasSummary)
            {
                await taskOrchestration.PostTaskNoteAsync(taskId,
                    $"📋 Review requested by {context.AgentName}: {(string)summaryObj!}");
            }
            else
            {
                await taskOrchestration.PostTaskNoteAsync(taskId,
                    $"📋 Review requested by {context.AgentName}");
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = updated.Id,
                    ["title"] = updated.Title,
                    ["status"] = updated.Status.ToString(),
                    ["previousStatus"] = task.Status.ToString(),
                    ["reviewRounds"] = updated.ReviewRounds,
                    ["message"] = $"Task '{updated.Title}' submitted for review (was {task.Status})"
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
