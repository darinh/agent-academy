using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles POST_PR_REVIEW — posts a review on a task's GitHub pull request.
/// Supports APPROVE, REQUEST_CHANGES, and COMMENT actions.
/// Role gate: Planner, Reviewer, or Human (agents should not self-review).
/// </summary>
public sealed class PostPrReviewHandler : ICommandHandler
{
    private readonly IGitHubService _gitHubService;

    public PostPrReviewHandler(IGitHubService gitHubService)
    {
        _gitHubService = gitHubService;
    }

    public string CommandName => "POST_PR_REVIEW";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Role gate: only Planner, Reviewer, or Human can post reviews
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Reviewer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only Planner, Reviewer, or Human roles can post PR reviews"
            };
        }

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

        // Parse body
        if (!command.Args.TryGetValue("body", out var bodyObj) || bodyObj is not string body
            || string.IsNullOrWhiteSpace(body))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: body"
            };
        }

        // Parse action (default: COMMENT)
        var action = PrReviewAction.Comment;
        if (command.Args.TryGetValue("action", out var actionObj) && actionObj is string actionStr
            && !string.IsNullOrWhiteSpace(actionStr))
        {
            action = actionStr.Trim().ToUpperInvariant() switch
            {
                "APPROVE" => PrReviewAction.Approve,
                "REQUEST_CHANGES" => PrReviewAction.RequestChanges,
                "COMMENT" => PrReviewAction.Comment,
                _ => PrReviewAction.Comment
            };

            // Validate known values
            if (actionStr.Trim().ToUpperInvariant() is not ("APPROVE" or "REQUEST_CHANGES" or "COMMENT"))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = $"Invalid action '{actionStr}'. Must be APPROVE, REQUEST_CHANGES, or COMMENT."
                };
            }
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

        // Validate task has a PR
        if (!task.PullRequestNumber.HasValue)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = "Task has no pull request — create one first with CREATE_PR"
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

        try
        {
            await _gitHubService.PostPrReviewAsync(task.PullRequestNumber.Value, body, action);

            var actionLabel = action switch
            {
                PrReviewAction.Approve => "approved",
                PrReviewAction.RequestChanges => "requested changes on",
                _ => "commented on"
            };

            // Post system message
            if (!string.IsNullOrWhiteSpace(context.RoomId))
            {
                await runtime.PostSystemStatusAsync(context.RoomId,
                    $"📝 {context.AgentName} {actionLabel} PR #{task.PullRequestNumber} for task \"{task.Title}\"");
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["prNumber"] = task.PullRequestNumber.Value,
                    ["action"] = action.ToString(),
                    ["message"] = $"Successfully {actionLabel} PR #{task.PullRequestNumber}"
                }
            };
        }
        catch (Exception ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Failed to post PR review: {ex.Message}"
            };
        }
    }
}
