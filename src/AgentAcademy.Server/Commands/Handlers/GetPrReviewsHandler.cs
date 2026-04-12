using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles GET_PR_REVIEWS — retrieves all reviews on a task's GitHub pull request.
/// Role gate: assigned agent, Planner, Reviewer, or Human.
/// </summary>
public sealed class GetPrReviewsHandler : ICommandHandler
{
    private readonly IGitHubService _gitHubService;

    public GetPrReviewsHandler(IGitHubService gitHubService)
    {
        _gitHubService = gitHubService;
    }

    public string CommandName => "GET_PR_REVIEWS";

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

        var taskQueries = context.Services.GetRequiredService<TaskQueryService>();

        // Load task
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

        // Role gate: assigned agent, Planner, Reviewer, or Human
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Reviewer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.AgentId, task.AssignedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only the assigned agent, Planner, Reviewer, or Human can view PR reviews"
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
            var reviews = await _gitHubService.GetPrReviewsAsync(task.PullRequestNumber.Value);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["prNumber"] = task.PullRequestNumber.Value,
                    ["reviewCount"] = reviews.Count,
                    ["reviews"] = reviews.Select(r => new Dictionary<string, object?>
                    {
                        ["author"] = r.Author,
                        ["body"] = r.Body,
                        ["state"] = r.State,
                        ["submittedAt"] = r.SubmittedAt?.ToString("O")
                    }).ToList()
                }
            };
        }
        catch (Exception ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Failed to fetch PR reviews: {ex.Message}"
            };
        }
    }
}
