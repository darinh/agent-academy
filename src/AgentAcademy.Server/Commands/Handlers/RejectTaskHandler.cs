using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles REJECT_TASK — reverts a task from Approved or Completed back to ChangesRequested.
/// For completed (merged) tasks, also reverts the merge commit on develop.
/// Reopens the task's breakout room so the assigned agent can address the findings.
/// </summary>
public sealed class RejectTaskHandler : ICommandHandler
{
    private readonly IGitService _gitService;

    public RejectTaskHandler(IGitService gitService)
    {
        _gitService = gitService;
    }

    public string CommandName => "REJECT_TASK";
    public bool IsDestructive => true;
    public string DestructiveWarning => "REJECT_TASK will revert the task status and may revert a merge commit on develop. The breakout room will be reopened.";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Reviewer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only Planner, Reviewer, or Human roles can reject tasks"
            };
        }

        if (!TryGetArg(command.Args, "taskId", out var taskId) &&
            !TryGetArg(command.Args, "value", out taskId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: taskId"
            };
        }

        if (!TryGetArg(command.Args, "reason", out var reason))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: reason"
            };
        }

        var taskOrchestration = context.Services.GetRequiredService<ITaskOrchestrationService>();
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

        if (task.Status != Shared.Models.TaskStatus.Approved && task.Status != Shared.Models.TaskStatus.Completed)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Task must be in Approved or Completed status to reject (current: {task.Status})"
            };
        }

        try
        {
            string? revertCommitSha = null;

            // For completed tasks with a merge commit, revert on develop first
            if (task.Status == Shared.Models.TaskStatus.Completed && !string.IsNullOrWhiteSpace(task.MergeCommitSha))
            {
                revertCommitSha = await _gitService.RevertCommitAsync(task.MergeCommitSha);
            }

            TaskSnapshot updated;
            try
            {
                updated = await taskOrchestration.RejectTaskAsync(taskId, context.AgentId, reason, revertCommitSha);
            }
            catch when (revertCommitSha is not null)
            {
                // Git revert succeeded but DB update failed — warn about inconsistency
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Execution,
                    Error = $"Git revert succeeded (commit {revertCommitSha}) but task status update failed. " +
                            $"The merge was reverted on develop but the task may still show as Completed. " +
                            $"Manual intervention required."
                };
            }

            var result = new Dictionary<string, object?>
            {
                ["taskId"] = updated.Id,
                ["title"] = updated.Title,
                ["status"] = updated.Status.ToString(),
                ["reviewerAgentId"] = updated.ReviewerAgentId,
                ["reviewRounds"] = updated.ReviewRounds,
                ["message"] = $"Task '{updated.Title}' rejected — returned to ChangesRequested"
            };

            if (revertCommitSha is not null)
                result["revertCommitSha"] = revertCommitSha;

            return command with
            {
                Status = CommandStatus.Success,
                Result = result
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

    private static bool TryGetArg(IReadOnlyDictionary<string, object?> args, string key, out string value)
    {
        value = string.Empty;
        return args.TryGetValue(key, out var obj) && obj is string s && !string.IsNullOrWhiteSpace(s)
            && (value = s.Trim()) == value;
    }
}
