using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CLAIM_TASK — assigns the calling agent to a task, preventing duplicate claims.
/// Auto-activates tasks in Queued status.
/// </summary>
public sealed class ClaimTaskHandler : ICommandHandler
{
    public string CommandName => "CLAIM_TASK";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("taskId", out var taskIdObj) || taskIdObj is not string taskId
            || string.IsNullOrWhiteSpace(taskId))
        {
            // Also accept "value" key (single-arg shorthand: CLAIM_TASK: task-123)
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

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();

        try
        {
            var task = await runtime.ClaimTaskAsync(taskId, context.AgentId, context.AgentName);
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = task.Id,
                    ["title"] = task.Title,
                    ["status"] = task.Status.ToString(),
                    ["assignedTo"] = task.AssignedAgentName,
                    ["message"] = $"Task '{task.Title}' claimed successfully"
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
