using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_FORGE_RUNS — lists all forge jobs with optional status filter.
/// Returns in-memory job tracking data (not durable across server restarts).
/// </summary>
public sealed class ListForgeRunsHandler : ICommandHandler
{
    public string CommandName => "LIST_FORGE_RUNS";
    public bool IsRetrySafe => true;

    public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var options = context.Services.GetRequiredService<ForgeOptions>();
        if (!options.Enabled)
        {
            return Task.FromResult(command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Forge engine is disabled on this server."
            });
        }

        var jobService = context.Services.GetRequiredService<IForgeJobService>();
        var jobs = jobService.ListJobs();

        // Optional status filter
        if (command.Args.TryGetValue("status", out var statusObj) && statusObj is string statusStr
            && !string.IsNullOrWhiteSpace(statusStr))
        {
            if (!Enum.TryParse<ForgeJobStatus>(statusStr, ignoreCase: true, out var filterStatus))
            {
                return Task.FromResult(command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = $"Invalid status filter '{statusStr}'. Valid values: queued, running, completed, failed."
                });
            }

            jobs = jobs.Where(j => j.Status == filterStatus).ToList();
        }

        var result = jobs.Select(j => new Dictionary<string, object?>
        {
            ["jobId"] = j.JobId,
            ["runId"] = j.RunId,
            ["status"] = j.Status.ToString().ToLowerInvariant(),
            ["error"] = j.Error,
            ["createdAt"] = j.CreatedAt.ToString("O"),
            ["completedAt"] = j.CompletedAt?.ToString("O"),
            ["taskId"] = j.TaskBrief.TaskId,
            ["taskTitle"] = j.TaskBrief.Title
        }).ToList();

        return Task.FromResult(command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["jobs"] = result,
                ["count"] = result.Count
            }
        });
    }
}
