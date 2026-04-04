using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CREATE_PR — pushes a task branch to GitHub and opens a pull request.
/// Updates the task entity with PR URL, number, and status.
/// Role gate: assigned agent, Planner, Reviewer, or Human.
/// </summary>
public sealed class CreatePrHandler : ICommandHandler
{
    private readonly GitService _gitService;
    private readonly IGitHubService _gitHubService;

    public CreatePrHandler(GitService gitService, IGitHubService gitHubService)
    {
        _gitService = gitService;
        _gitHubService = gitHubService;
    }

    public string CommandName => "CREATE_PR";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Parse taskId
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

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();

        // Load task
        var task = await runtime.GetTaskAsync(taskId);
        if (task is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Task '{taskId}' not found"
            };
        }

        // For non-privileged roles, verify the agent is the task assignee
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Reviewer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentId, task.AssignedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only the assigned agent, Planner, Reviewer, or Human can create a PR for this task"
            };
        }

        // Validate task has a branch
        if (string.IsNullOrWhiteSpace(task.BranchName))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = "Task has no branch — work must be started before creating a PR"
            };
        }

        // Validate task doesn't already have a PR
        if (task.PullRequestNumber.HasValue)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Task already has PR #{task.PullRequestNumber} ({task.PullRequestUrl})"
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

        // Optional args
        var title = command.Args.TryGetValue("title", out var titleObj) && titleObj is string t
            ? t : task.Title;
        var body = command.Args.TryGetValue("body", out var bodyObj) && bodyObj is string b
            ? b : $"## {task.Title}\n\n{task.Description}\n\n### Success Criteria\n{task.SuccessCriteria}";
        var baseBranch = command.Args.TryGetValue("baseBranch", out var baseObj) && baseObj is string bb
            ? bb : "develop";

        try
        {
            // Push the branch to remote
            await _gitService.PushBranchAsync(task.BranchName);

            // Create the PR
            PullRequestInfo? pr = null;
            try
            {
                pr = await _gitHubService.CreatePullRequestAsync(
                    task.BranchName, title, body, baseBranch);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // PR already exists for this branch (partial failure recovery)
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Conflict,
                    Error = $"A pull request already exists for branch '{task.BranchName}'. " +
                            "The task entity may need manual PR update."
                };
            }

            // Update task with PR info — if this fails, include PR details in the error
            try
            {
                await runtime.UpdateTaskPrAsync(
                    taskId, pr.Url, pr.Number, PullRequestStatus.Open);
            }
            catch (Exception updateEx)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Execution,
                    Error = $"PR #{pr.Number} was created ({pr.Url}) but task update failed: {updateEx.Message}. " +
                            "Retry or manually update the task."
                };
            }

            // Post success note
            if (!string.IsNullOrWhiteSpace(context.RoomId))
            {
                await runtime.PostSystemStatusAsync(context.RoomId,
                    $"🔗 PR #{pr.Number} created for task \"{task.Title}\": {pr.Url}");
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["prNumber"] = pr.Number,
                    ["prUrl"] = pr.Url,
                    ["branch"] = task.BranchName,
                    ["baseBranch"] = baseBranch,
                    ["message"] = $"PR #{pr.Number} created: {pr.Url}"
                }
            };
        }
        catch (Exception ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Failed to create PR: {ex.Message}"
            };
        }
    }
}
