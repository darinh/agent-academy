using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles ADD_TASK_COMMENT — attaches a comment, finding, evidence, or blocker note to a task.
/// The calling agent must be the task assignee, a reviewer, or a Planner.
/// </summary>
public sealed class AddTaskCommentHandler : ICommandHandler
{
    public string CommandName => "ADD_TASK_COMMENT";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("taskId", out var taskIdObj) || taskIdObj is not string taskId
            || string.IsNullOrWhiteSpace(taskId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = "Missing required argument: TaskId"
            };
        }

        if (!command.Args.TryGetValue("content", out var contentObj) || contentObj is not string content
            || string.IsNullOrWhiteSpace(content))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = "Missing required argument: Content"
            };
        }

        var commentType = TaskCommentType.Comment;
        if (command.Args.TryGetValue("type", out var typeObj) && typeObj is string typeStr
            && !string.IsNullOrWhiteSpace(typeStr))
        {
            if (!Enum.TryParse(typeStr, ignoreCase: true, out commentType))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    Error = $"Invalid comment type: '{typeStr}'. Valid types: Comment, Finding, Evidence, Blocker"
                };
            }
        }

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();

        try
        {
            // Validate agent can comment on this task
            var task = await runtime.GetTaskAsync(taskId);
            if (task is null)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    Error = $"Task '{taskId}' not found"
                };
            }

            var isAssignee = string.Equals(task.AssignedAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase);
            var isReviewer = string.Equals(task.ReviewerAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase);
            var isPlanner = string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase);

            if (!isAssignee && !isReviewer && !isPlanner)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    Error = $"Only the assignee, reviewer, or planner can comment on task '{taskId}'"
                };
            }

            var comment = await runtime.AddTaskCommentAsync(
                taskId, context.AgentId, context.AgentName, commentType, content);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["commentId"] = comment.Id,
                    ["taskId"] = comment.TaskId,
                    ["type"] = comment.CommentType.ToString(),
                    ["message"] = $"{commentType} added to task '{task.Title}'"
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = ex.Message
            };
        }
    }
}
