using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_TASK_HISTORY — returns an interleaved chronological list
/// of comments and evidence records for a task.
/// </summary>
public sealed class ShowTaskHistoryHandler : ICommandHandler
{
    public string CommandName => "SHOW_TASK_HISTORY";
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

        var comments = await taskQueries.GetTaskCommentsAsync(taskId);
        var evidence = await taskQueries.GetTaskEvidenceAsync(taskId);

        // Interleave comments and evidence by timestamp
        var entries = new List<Dictionary<string, object?>>();

        foreach (var c in comments)
        {
            entries.Add(new Dictionary<string, object?>
            {
                ["entryType"] = "comment",
                ["id"] = c.Id,
                ["agent"] = c.AgentName,
                ["commentType"] = c.CommentType.ToString(),
                ["content"] = c.Content,
                ["timestamp"] = c.CreatedAt.ToString("o")
            });
        }

        foreach (var e in evidence)
        {
            entries.Add(new Dictionary<string, object?>
            {
                ["entryType"] = "evidence",
                ["id"] = e.Id,
                ["agent"] = e.AgentName,
                ["phase"] = e.Phase.ToString(),
                ["checkName"] = e.CheckName,
                ["tool"] = e.Tool,
                ["passed"] = e.Passed,
                ["output"] = e.OutputSnippet,
                ["timestamp"] = e.CreatedAt.ToString("o")
            });
        }

        // Sort by timestamp descending (newest first), then take count
        var sorted = entries
            .OrderByDescending(e => e["timestamp"]?.ToString() ?? "")
            .Take(count)
            .ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["taskId"] = taskId,
                ["taskTitle"] = task.Title,
                ["entries"] = sorted,
                ["count"] = sorted.Count,
                ["totalComments"] = comments.Count,
                ["totalEvidence"] = evidence.Count,
                ["message"] = sorted.Count == 0
                    ? $"No history for task '{task.Title}'"
                    : $"{sorted.Count} history entries for task '{task.Title}'"
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
