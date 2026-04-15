using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles MERGE_PR — squash-merges a task's pull request via the GitHub API.
/// Only Planner, Reviewer, or Human roles may invoke this command.
/// The task must be in Approved status and have a pull request associated.
/// After merge, the task transitions to Completed with the merge commit SHA.
/// </summary>
public sealed class MergePrHandler : ICommandHandler
{
    private readonly IGitHubService _gitHubService;

    public MergePrHandler(IGitHubService gitHubService)
    {
        _gitHubService = gitHubService;
    }

    public string CommandName => "MERGE_PR";

    private static string BuildCommitTitle(TaskType taskType, string title)
        => $"{GetConventionalCommitPrefix(taskType)}{title}";

    private static string GetConventionalCommitPrefix(TaskType taskType)
        => taskType switch
        {
            TaskType.Feature => "feat: ",
            TaskType.Bug => "fix: ",
            TaskType.Chore => "chore: ",
            TaskType.Spike => "docs: ",
            _ => throw new ArgumentOutOfRangeException(
                nameof(taskType), taskType,
                $"No conventional commit prefix defined for TaskType '{taskType}'.")
        };

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Role gate: Planner, Reviewer, or Human
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Reviewer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only Planner, Reviewer, or Human roles can merge PRs"
            };
        }

        // Parse taskId
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

        var messages = context.Services.GetRequiredService<MessageService>();
        var taskLifecycle = context.Services.GetRequiredService<ITaskLifecycleService>();
        var taskOrchestration = context.Services.GetRequiredService<TaskOrchestrationService>();
        var taskQueries = context.Services.GetRequiredService<ITaskQueryService>();

        // Validate task exists
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

        // Validate task is Approved
        if (task.Status != Shared.Models.TaskStatus.Approved)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Task must be in Approved status to merge (current: {task.Status})"
            };
        }

        // Validate task has a PR
        if (!task.PullRequestNumber.HasValue)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = "Task has no pull request — use CREATE_PR first, or use MERGE_TASK for local merge"
            };
        }

        // Verify GitHub is configured
        if (!await _gitHubService.IsConfiguredAsync())
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "GitHub CLI is not authenticated. Run 'gh auth login' on the server."
            };
        }

        // Parse optional deleteBranch arg
        var deleteBranch = command.Args.TryGetValue("deleteBranch", out var deleteObj)
            && deleteObj is string deleteStr
            && string.Equals(deleteStr, "true", StringComparison.OrdinalIgnoreCase);

        // Set task to Merging status before git ops
        await taskQueries.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Merging);

        // Phase 1: Attempt the GitHub merge
        PrMergeResult result;
        try
        {
            var commitTitle = BuildCommitTitle(task.Type, task.Title);
            result = await _gitHubService.MergePullRequestAsync(
                task.PullRequestNumber.Value, commitTitle, deleteBranch);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("already been merged", StringComparison.OrdinalIgnoreCase))
        {
            // PR was merged externally — finalize locally instead of failing
            try
            {
                await taskLifecycle.SyncTaskPrStatusAsync(taskId, PullRequestStatus.Merged);
                await taskOrchestration.CompleteTaskAsync(taskId, commitCount: 1);

                return command with
                {
                    Status = CommandStatus.Success,
                    Result = new Dictionary<string, object?>
                    {
                        ["taskId"] = task.Id,
                        ["title"] = task.Title,
                        ["prNumber"] = task.PullRequestNumber,
                        ["prUrl"] = task.PullRequestUrl,
                        ["mergeCommitSha"] = (object?)null,
                        ["message"] = $"PR #{task.PullRequestNumber} was already merged — task finalized"
                    }
                };
            }
            catch (Exception finalizeEx)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Execution,
                    Error = $"PR was already merged but local finalization failed: {finalizeEx.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            // Merge failed — revert task to Approved
            try
            {
                await taskQueries.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Approved);
            }
            catch (Exception recoveryEx)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Execution,
                    Error = $"PR merge failed: {ex.Message} (task status recovery also failed: {recoveryEx.Message})"
                };
            }

            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"PR merge failed: {ex.Message}"
            };
        }

        // Phase 2: Merge succeeded on GitHub — finalize locally
        // Do NOT rollback to Approved here — the PR is already merged
        try
        {
            await taskLifecycle.SyncTaskPrStatusAsync(taskId, PullRequestStatus.Merged);
            await taskOrchestration.CompleteTaskAsync(taskId, commitCount: 1, mergeCommitSha: result.MergeCommitSha);
        }
        catch (Exception ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"PR #{task.PullRequestNumber} was merged on GitHub but local task update failed: {ex.Message}. " +
                        "The PR is merged — retry MERGE_PR or manually complete the task."
            };
        }

        // Post success note to task room (best-effort)
        if (!string.IsNullOrWhiteSpace(context.RoomId))
        {
            try
            {
                await messages.PostSystemStatusAsync(context.RoomId,
                    $"✅ PR #{task.PullRequestNumber} for task \"{task.Title}\" merged via GitHub.");
            }
            catch
            {
                // Notification is best-effort — merge already succeeded
            }
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["taskId"] = task.Id,
                ["title"] = task.Title,
                ["prNumber"] = task.PullRequestNumber,
                ["prUrl"] = task.PullRequestUrl,
                ["mergeCommitSha"] = result.MergeCommitSha,
                ["message"] = $"PR #{task.PullRequestNumber} merged successfully via GitHub"
            }
        };
    }
}
