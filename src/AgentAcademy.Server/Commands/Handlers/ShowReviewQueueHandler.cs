using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_REVIEW_QUEUE — returns tasks pending review (InReview or AwaitingValidation).
/// </summary>
public sealed class ShowReviewQueueHandler : ICommandHandler
{
    public string CommandName => "SHOW_REVIEW_QUEUE";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var taskQueries = context.Services.GetRequiredService<ITaskQueryService>();
        var tasks = await taskQueries.GetReviewQueueAsync();

        var result = tasks.Select(t => new Dictionary<string, object?>
        {
            ["id"] = t.Id,
            ["title"] = t.Title,
            ["description"] = t.Description,
            ["type"] = t.Type.ToString(),
            ["status"] = t.Status.ToString(),
            ["assignedTo"] = t.AssignedAgentName ?? t.AssignedAgentId,
            ["reviewerAgentId"] = t.ReviewerAgentId,
            ["reviewRounds"] = t.ReviewRounds,
            ["branchName"] = t.BranchName,
            ["commitCount"] = t.CommitCount,
            ["commentCount"] = t.CommentCount,
            ["createdAt"] = t.CreatedAt.ToString("o")
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["tasks"] = result,
                ["count"] = result.Count,
                ["message"] = result.Count == 0
                    ? "No tasks pending review"
                    : $"{result.Count} task(s) pending review"
            }
        };
    }
}
