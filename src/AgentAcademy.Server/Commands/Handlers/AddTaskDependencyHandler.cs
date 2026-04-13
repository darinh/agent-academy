using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles ADD_TASK_DEPENDENCY — declares that one task depends on another.
/// </summary>
public sealed class AddTaskDependencyHandler : ICommandHandler
{
    public string CommandName => "ADD_TASK_DEPENDENCY";

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

        var depService = context.Services.GetRequiredService<TaskDependencyService>();

        try
        {
            var info = await depService.AddDependencyAsync(taskId, dependsOnTaskId);
            var blockCount = info.DependsOn.Count(d => !d.IsSatisfied);
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["dependsOnTaskId"] = dependsOnTaskId,
                    ["totalDependencies"] = info.DependsOn.Count,
                    ["unmetDependencies"] = blockCount,
                    ["message"] = blockCount > 0
                        ? $"Dependency added. Task has {blockCount} unmet dependency(ies) — cannot be started yet."
                        : "Dependency added. All dependencies are satisfied."
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
