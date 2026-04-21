using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_DECISIONS — returns task comments tagged as Decision type,
/// giving agents a focused view of key decisions recorded on a task.
/// </summary>
public sealed class ShowDecisionsHandler : ICommandHandler
{
    public string CommandName => "SHOW_DECISIONS";
    public bool IsRetrySafe => true;

    private const int DefaultCount = 20;
    private const int MaxCount = 50;

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

        var count = DefaultCount;
        if (command.Args.TryGetValue("count", out var countObj))
        {
            if (countObj is string countStr && int.TryParse(countStr, out var parsed))
                count = parsed;
            else if (countObj is int intVal)
                count = intVal;
            else if (countObj is long longVal)
                count = longVal > MaxCount ? MaxCount : (int)longVal;
        }

        if (count < 1) count = 1;
        if (count > MaxCount) count = MaxCount;

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

        var allComments = await taskQueries.GetTaskCommentsAsync(taskId);
        var decisions = allComments
            .Where(c => c.CommentType == TaskCommentType.Decision)
            .OrderByDescending(c => c.CreatedAt)
            .Take(count)
            .Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["agent"] = c.AgentName,
                ["content"] = c.Content,
                ["timestamp"] = c.CreatedAt.ToString("o")
            })
            .ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["taskId"] = taskId,
                ["taskTitle"] = task.Title,
                ["decisions"] = decisions,
                ["count"] = decisions.Count,
                ["totalComments"] = allComments.Count,
                ["message"] = decisions.Count == 0
                    ? $"No decisions recorded for task '{task.Title}'"
                    : $"{decisions.Count} decision(s) for task '{task.Title}'"
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
