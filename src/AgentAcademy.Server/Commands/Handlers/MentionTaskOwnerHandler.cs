using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles MENTION_TASK_OWNER — sends a targeted DM to the agent assigned to
/// a specific task. Useful for reviewers flagging issues to implementers,
/// architects raising concerns, or planners nudging blocked assignees.
/// </summary>
public sealed class MentionTaskOwnerHandler : ICommandHandler
{
    public string CommandName => "MENTION_TASK_OWNER";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("taskId", out var taskIdObj) || taskIdObj is not string taskId ||
            string.IsNullOrWhiteSpace(taskId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required arg: taskId. Usage: MENTION_TASK_OWNER:\n  TaskId: <task-id>\n  Message: <your message>"
            };
        }

        if (!command.Args.TryGetValue("message", out var messageObj) || messageObj is not string message ||
            string.IsNullOrWhiteSpace(message))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required arg: message. Usage: MENTION_TASK_OWNER:\n  TaskId: <task-id>\n  Message: <your message>"
            };
        }

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

        if (string.IsNullOrWhiteSpace(task.AssignedAgentId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Task '{task.Title}' has no assigned agent"
            };
        }

        if (string.Equals(task.AssignedAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = "Cannot mention yourself — you are the task owner"
            };
        }

        // Validate the assignee still exists in the catalog
        var catalog = context.Services.GetRequiredService<IAgentCatalog>();
        var targetAgent = catalog.Agents.FirstOrDefault(
            a => string.Equals(a.Id, task.AssignedAgentId, StringComparison.OrdinalIgnoreCase));

        if (targetAgent is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Assigned agent '{task.AssignedAgentId}' is no longer in the catalog"
            };
        }

        var messages = context.Services.GetRequiredService<IMessageService>();
        var roomId = context.RoomId ?? "main";
        var dmContent = $"[Re: task '{task.Title}' ({task.Id})] {message}";

        await messages.SendDirectMessageAsync(
            context.AgentId, context.AgentName ?? context.AgentId,
            context.AgentRole ?? "Agent", targetAgent.Id, dmContent, roomId);

        // Wake the recipient so they respond promptly
        var orchestrator = context.Services.GetRequiredService<IAgentOrchestrator>();
        orchestrator.HandleDirectMessage(targetAgent.Id);

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["taskId"] = task.Id,
                ["taskTitle"] = task.Title,
                ["recipient"] = targetAgent.Name,
                ["recipientId"] = targetAgent.Id,
                ["message"] = $"Mentioned {targetAgent.Name} about task '{task.Title}'. They will respond in their next turn."
            }
        };
    }
}
