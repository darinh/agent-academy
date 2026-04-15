using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles MERGE_TASK — squash-merges a task branch into develop.
/// Only Reviewer or Planner roles may invoke this command.
/// The task must be in Approved status and have a branch name set.
/// After a successful merge, fires a post-task retrospective (fire-and-forget).
/// </summary>
public sealed class MergeTaskHandler : ICommandHandler
{
    private readonly GitService _gitService;
    private readonly ILogger<MergeTaskHandler> _logger;

    public MergeTaskHandler(
        GitService gitService,
        ILogger<MergeTaskHandler> logger)
    {
        _gitService = gitService;
        _logger = logger;
    }

    public string CommandName => "MERGE_TASK";
    public bool IsDestructive => true;
    public string DestructiveWarning => "MERGE_TASK will squash-merge the task branch into develop. This is irreversible without a revert commit.";

    private static string BuildCommitMessage(TaskType taskType, string title)
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

    /// <summary>
    /// Post-merge-failure conflict detection. Returns a human-readable conflict
    /// summary, or null if detection itself fails.
    /// </summary>
    private async Task<string?> TryDetectConflictsAsync(string branch)
    {
        try
        {
            var result = await _gitService.DetectMergeConflictsAsync(branch);
            if (result.HasConflicts && result.ConflictingFiles.Count > 0)
            {
                return string.Join(", ", result.ConflictingFiles);
            }
        }
        catch
        {
            // Conflict detection is best-effort — don't hide the original merge error
        }
        return null;
    }

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Reviewer", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only Planner or Reviewer roles can merge tasks"
            };
        }

        // Parse taskId from args or value
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
                    Error = "Missing required argument: TaskId"
                };
            }
            taskId = taskIdValue;
        }

        var messages = context.Services.GetRequiredService<MessageService>();
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

        // Validate task has a branch
        if (string.IsNullOrWhiteSpace(task.BranchName))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = "Task has no branch to merge"
            };
        }

        try
        {
            // Set task to Merging status before git ops
            await taskQueries.UpdateTaskStatusAsync(taskId, Shared.Models.TaskStatus.Merging);

            var commitMessage = BuildCommitMessage(task.Type, task.Title);
            var mergeCommitSha = await _gitService.SquashMergeAsync(task.BranchName, commitMessage, context.GitIdentity);
            await taskOrchestration.CompleteTaskAsync(taskId, commitCount: 1, mergeCommitSha: mergeCommitSha);

            // Post success note to task room
            if (!string.IsNullOrWhiteSpace(context.RoomId))
            {
                await messages.PostSystemStatusAsync(context.RoomId,
                    $"✅ Task \"{task.Title}\" merged from {task.BranchName} into develop.");
            }

            // Fire-and-forget: run post-task retrospective for the assigned agent
            // Resolved at execution time to break DI cycle:
            // RetrospectiveService → CommandPipeline → MergeTaskHandler → RetrospectiveService
            var retrospective = context.Services.GetRequiredService<RetrospectiveService>();
            var retroTaskId = taskId;
            var retroAgentId = task.AssignedAgentId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await retrospective.RunRetrospectiveAsync(retroTaskId, retroAgentId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Post-merge retrospective failed for task {TaskId}", retroTaskId);
                }
            });

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = task.Id,
                    ["title"] = task.Title,
                    ["branch"] = task.BranchName,
                    ["mergeCommitSha"] = mergeCommitSha,
                    ["message"] = $"Task '{task.Title}' merged successfully"
                }
            };
        }
        catch (Exception ex)
        {
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
                    Error = $"Merge failed: {ex.Message} (task status recovery also failed: {recoveryEx.Message})"
                };
            }

            // Attempt to detect which files conflicted for actionable feedback
            var conflictHint = await TryDetectConflictsAsync(task.BranchName!);

            if (conflictHint is not null)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Conflict,
                    Error = $"Merge conflict: {conflictHint}. Use REBASE_TASK to rebase the branch onto develop, then retry."
                };
            }

            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Merge failed: {ex.Message}"
            };
        }
    }
}
