using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_AGENT_STATS — returns per-agent task effectiveness metrics
/// so the planner can make data-driven task assignments.
/// </summary>
public sealed class ListAgentStatsHandler : ICommandHandler
{
    public string CommandName => "LIST_AGENT_STATS";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        int? hoursBack = null;
        if (command.Args.TryGetValue("hoursBack", out var hbObj))
        {
            if (hbObj is string hbStr && int.TryParse(hbStr, out var hb) && hb >= 1 && hb <= 8760)
                hoursBack = hb;
            else if (hbObj is int hbInt && hbInt >= 1 && hbInt <= 8760)
                hoursBack = hbInt;
            else if (hbObj is long hbLong && hbLong >= 1 && hbLong <= 8760)
                hoursBack = (int)hbLong;
            else
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "hoursBack must be an integer between 1 and 8760."
                };
        }

        string? agentFilter = null;
        if (command.Args.TryGetValue("agentId", out var agentObj) && agentObj is string agentStr)
            agentFilter = agentStr;

        var taskAnalytics = context.Services.GetRequiredService<TaskAnalyticsService>();
        var analytics = await taskAnalytics.GetTaskCycleAnalyticsAsync(hoursBack);

        var agents = analytics.AgentEffectiveness;

        if (agentFilter is not null)
        {
            agents = agents
                .Where(a => a.AgentId.Equals(agentFilter, StringComparison.OrdinalIgnoreCase)
                         || a.AgentName.Equals(agentFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var result = agents.Select(a => new Dictionary<string, object?>
        {
            ["agentId"] = a.AgentId,
            ["agentName"] = a.AgentName,
            ["assigned"] = a.Assigned,
            ["completed"] = a.Completed,
            ["cancelled"] = a.Cancelled,
            ["completionRate"] = FormatPercent(a.CompletionRate),
            ["avgCycleTimeHours"] = a.AvgCycleTimeHours,
            ["avgReviewRounds"] = a.AvgReviewRounds,
            ["firstPassApprovalRate"] = FormatPercent(a.FirstPassApprovalRate),
            ["reworkRate"] = FormatPercent(a.ReworkRate),
            ["avgCommitsPerTask"] = a.AvgCommitsPerTask
        }).ToList();

        var overview = new Dictionary<string, object?>
        {
            ["totalTasks"] = analytics.Overview.TotalTasks,
            ["completionRate"] = FormatPercent(analytics.Overview.CompletionRate),
            ["avgCycleTimeHours"] = analytics.Overview.AvgCycleTimeHours,
            ["avgReviewRounds"] = analytics.Overview.AvgReviewRounds,
            ["reworkRate"] = FormatPercent(analytics.Overview.ReworkRate)
        };

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["overview"] = overview,
                ["agents"] = result,
                ["count"] = result.Count,
                ["windowStart"] = analytics.WindowStart.ToString("o"),
                ["windowEnd"] = analytics.WindowEnd.ToString("o")
            }
        };
    }

    private static string FormatPercent(double rate) => $"{rate * 100:F1}%";
}
