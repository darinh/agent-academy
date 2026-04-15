using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles REMOVE_TASK_DEPENDENCY — removes a dependency between two tasks.
/// </summary>
public sealed class RemoveTaskDependencyHandler : ICommandHandler
{
    public string CommandName => "REMOVE_TASK_DEPENDENCY";

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

        if (!command.Args.TryGetValue("dependsOnTaskId", out var depObj) || depObj is not string dependsOnTaskId
            || string.IsNullOrWhiteSpace(dependsOnTaskId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: dependsOnTaskId"
            };
        }

        var depService = context.Services.GetRequiredService<ITaskDependencyService>();

        try
        {
            var info = await depService.RemoveDependencyAsync(taskId, dependsOnTaskId);
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["dependsOnTaskId"] = dependsOnTaskId,
                    ["remainingDependencies"] = info.DependsOn.Count,
                    ["message"] = $"Dependency removed. Task now has {info.DependsOn.Count} dependency(ies)."
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
