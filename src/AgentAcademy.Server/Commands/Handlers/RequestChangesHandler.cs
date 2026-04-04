using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles REQUEST_CHANGES — reviewer requests changes on a task. Requires findings.
/// Only works on tasks in InReview or AwaitingValidation.
/// </summary>
public sealed class RequestChangesHandler : ICommandHandler
{
    public string CommandName => "REQUEST_CHANGES";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("taskId", out var taskIdObj) || taskIdObj is not string taskId
            || string.IsNullOrWhiteSpace(taskId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: taskId"
            };
        }

        if (!command.Args.TryGetValue("findings", out var findingsObj) || findingsObj is not string findings
            || string.IsNullOrWhiteSpace(findings))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: findings"
            };
        }

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();

        try
        {
            var task = await runtime.RequestChangesAsync(taskId, context.AgentId, findings);
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
                    ["message"] = $"Changes requested on task '{task.Title}'"
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
