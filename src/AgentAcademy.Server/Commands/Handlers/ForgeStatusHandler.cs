using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles FORGE_STATUS — returns forge engine status or details of a specific job.
/// When called with a jobId arg, returns that job's details.
/// When called without args, returns overall engine health summary.
/// </summary>
public sealed class ForgeStatusHandler : ICommandHandler
{
    public string CommandName => "FORGE_STATUS";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var options = context.Services.GetRequiredService<ForgeOptions>();
        if (!options.Enabled)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Forge engine is disabled on this server."
            };
        }

        var jobService = context.Services.GetRequiredService<IForgeJobService>();

        // If jobId provided, return specific job details
        if (command.Args.TryGetValue("jobId", out var jobIdObj) && jobIdObj is string jobId
            && !string.IsNullOrWhiteSpace(jobId))
        {
            var job = await jobService.GetJobAsync(jobId);
            if (job is null)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.NotFound,
                    Error = $"Forge job '{jobId}' not found."
                };
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["jobId"] = job.JobId,
                    ["runId"] = job.RunId,
                    ["status"] = job.Status.ToString().ToLowerInvariant(),
                    ["error"] = job.Error,
                    ["createdAt"] = job.CreatedAt.ToString("O"),
                    ["startedAt"] = job.StartedAt?.ToString("O"),
                    ["completedAt"] = job.CompletedAt?.ToString("O"),
                    ["taskId"] = job.TaskBrief.TaskId,
                    ["taskTitle"] = job.TaskBrief.Title
                }
            };
        }

        // No jobId — return engine status summary
        var jobs = await jobService.ListJobsAsync();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["enabled"] = options.Enabled,
                ["executionAvailable"] = options.ExecutionAvailable,
                ["runsDirectory"] = options.RunsDirectory,
                ["activeJobs"] = jobs.Count(j => j.Status is ForgeJobStatus.Queued or ForgeJobStatus.Running),
                ["totalJobs"] = jobs.Count,
                ["completedJobs"] = jobs.Count(j => j.Status == ForgeJobStatus.Completed),
                ["failedJobs"] = jobs.Count(j => j.Status == ForgeJobStatus.Failed)
            }
        };
    }
}
