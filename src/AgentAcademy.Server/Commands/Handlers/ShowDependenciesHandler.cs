using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_DEPENDENCIES — returns the dependency graph for a task,
/// including what it depends on and what depends on it.
/// </summary>
public sealed class ShowDependenciesHandler : ICommandHandler
{
    public string CommandName => "SHOW_DEPENDENCIES";
    public bool IsRetrySafe => true;

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

        var depService = context.Services.GetRequiredService<ITaskDependencyService>();
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

        var deps = await depService.GetDependencyInfoAsync(taskId);

        var dependsOn = deps.DependsOn.Select(d => new Dictionary<string, object?>
        {
            ["taskId"] = d.TaskId,
            ["title"] = d.Title,
            ["status"] = d.Status.ToString(),
            ["satisfied"] = d.IsSatisfied
        }).ToList();

        var dependedOnBy = deps.DependedOnBy.Select(d => new Dictionary<string, object?>
        {
            ["taskId"] = d.TaskId,
            ["title"] = d.Title,
            ["status"] = d.Status.ToString(),
            ["satisfied"] = d.IsSatisfied
        }).ToList();

        var unmetCount = deps.DependsOn.Count(d => !d.IsSatisfied);
        var isBlocked = unmetCount > 0;

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["taskId"] = taskId,
                ["taskTitle"] = task.Title,
                ["taskStatus"] = task.Status.ToString(),
                ["dependsOn"] = dependsOn,
                ["dependedOnBy"] = dependedOnBy,
                ["totalUpstream"] = dependsOn.Count,
                ["totalDownstream"] = dependedOnBy.Count,
                ["unmetDependencies"] = unmetCount,
                ["isBlocked"] = isBlocked,
                ["message"] = isBlocked
                    ? $"Task '{task.Title}' is blocked by {unmetCount} unmet dependency(ies)"
                    : dependsOn.Count == 0 && dependedOnBy.Count == 0
                        ? $"Task '{task.Title}' has no dependencies"
                        : $"Task '{task.Title}': {dependsOn.Count} upstream, {dependedOnBy.Count} downstream — all met"
            }
        };
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
