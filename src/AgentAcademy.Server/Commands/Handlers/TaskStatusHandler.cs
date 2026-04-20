using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles TASK_STATUS — returns deep detail on a single task including
/// dependency info, evidence summary, and recent comments.
/// </summary>
public sealed class TaskStatusHandler : ICommandHandler
{
    public string CommandName => "TASK_STATUS";
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

        var taskQueries = context.Services.GetRequiredService<ITaskQueryService>();
        var depService = context.Services.GetRequiredService<ITaskDependencyService>();

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
        var evidence = await taskQueries.GetTaskEvidenceAsync(taskId);
        var commentCount = await taskQueries.GetTaskCommentCountAsync(taskId);
        var specLinks = await taskQueries.GetSpecLinksForTaskAsync(taskId);

        var dependsOn = deps.DependsOn.Select(d => new Dictionary<string, object?>
        {
            ["taskId"] = d.TaskId,
            ["title"] = d.Title,
            ["status"] = d.Status.ToString(),
            ["satisfied"] = d.IsSatisfied
        }).ToList();

        var blockedBy = deps.DependedOnBy.Select(d => new Dictionary<string, object?>
        {
            ["taskId"] = d.TaskId,
            ["title"] = d.Title,
            ["status"] = d.Status.ToString(),
            ["satisfied"] = d.IsSatisfied
        }).ToList();

        var evidenceSummary = new Dictionary<string, object?>
        {
            ["total"] = evidence.Count,
            ["passed"] = evidence.Count(e => e.Passed),
            ["failed"] = evidence.Count(e => !e.Passed),
            ["phases"] = evidence
                .GroupBy(e => e.Phase.ToString())
                .ToDictionary(g => g.Key, g => (object?)g.Count())
        };

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["id"] = task.Id,
                ["title"] = task.Title,
                ["description"] = task.Description,
                ["successCriteria"] = task.SuccessCriteria,
                ["status"] = task.Status.ToString(),
                ["type"] = task.Type.ToString(),
                ["priority"] = task.Priority.ToString(),
                ["size"] = task.Size?.ToString(),
                ["assignedTo"] = task.AssignedAgentName ?? task.AssignedAgentId,
                ["reviewerAgentId"] = task.ReviewerAgentId,
                ["reviewRounds"] = task.ReviewRounds,
                ["branchName"] = task.BranchName,
                ["pullRequestUrl"] = task.PullRequestUrl,
                ["pullRequestStatus"] = task.PullRequestStatus?.ToString(),
                ["mergeCommitSha"] = task.MergeCommitSha,
                ["commitCount"] = task.CommitCount,
                ["commentCount"] = commentCount,
                ["createdAt"] = task.CreatedAt.ToString("o"),
                ["startedAt"] = task.StartedAt?.ToString("o"),
                ["completedAt"] = task.CompletedAt?.ToString("o"),
                ["dependsOn"] = dependsOn,
                ["dependedOnBy"] = blockedBy,
                ["evidence"] = evidenceSummary,
                ["specLinks"] = specLinks.Select(l => l.SpecSectionId).ToList(),
                ["message"] = $"Task '{task.Title}' — {task.Status}"
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
