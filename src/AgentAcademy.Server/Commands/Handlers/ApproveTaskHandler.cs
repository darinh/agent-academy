using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles APPROVE_TASK — reviewer approves a task. Only works on tasks in InReview or AwaitingValidation.
/// </summary>
public sealed class ApproveTaskHandler : ICommandHandler
{
    public string CommandName => "APPROVE_TASK";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
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

        string? findings = null;
        if (command.Args.TryGetValue("findings", out var findingsObj) && findingsObj is string findingsStr
            && !string.IsNullOrWhiteSpace(findingsStr))
        {
            findings = findingsStr;
        }

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();

        try
        {
            var task = await runtime.ApproveTaskAsync(taskId, context.AgentId, findings);
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = task.Id,
                    ["title"] = task.Title,
                    ["status"] = task.Status.ToString(),
                    ["reviewerAgentId"] = task.ReviewerAgentId,
                    ["reviewRounds"] = task.ReviewRounds,
                    ["message"] = $"Task '{task.Title}' approved"
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
