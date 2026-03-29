using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RELEASE_TASK — unassigns the calling agent from a task.
/// Only the currently assigned agent can release.
/// </summary>
public sealed class ReleaseTaskHandler : ICommandHandler
{
    public string CommandName => "RELEASE_TASK";

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
                    Error = "Missing required argument: taskId"
                };
            }
            taskId = taskIdValue;
        }

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();

        try
        {
            var task = await runtime.ReleaseTaskAsync(taskId, context.AgentId);
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = task.Id,
                    ["title"] = task.Title,
                    ["status"] = task.Status.ToString(),
                    ["message"] = $"Task '{task.Title}' released successfully"
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
