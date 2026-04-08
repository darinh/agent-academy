using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles QUERY_EVIDENCE — retrieves the evidence ledger for a task.
/// Any agent can query evidence (read-only operation).
/// </summary>
public sealed class QueryEvidenceHandler : ICommandHandler
{
    public string CommandName => "QUERY_EVIDENCE";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
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

        // Parse optional phase filter
        EvidencePhase? phase = null;
        if (command.Args.TryGetValue("phase", out var phaseObj) && phaseObj is string phaseStr
            && !string.IsNullOrWhiteSpace(phaseStr))
        {
            if (!Enum.TryParse(phaseStr, ignoreCase: true, out EvidencePhase parsed))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = $"Invalid phase: '{phaseStr}'. Valid: Baseline, After, Review"
                };
            }
            phase = parsed;
        }

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();

        try
        {
            var evidence = await runtime.GetTaskEvidenceAsync(taskId, phase);

            var passedCount = evidence.Count(e => e.Passed);
            var failedCount = evidence.Count(e => !e.Passed);

            var rows = evidence.Select(e => new Dictionary<string, object?>
            {
                ["id"] = e.Id,
                ["phase"] = e.Phase.ToString(),
                ["checkName"] = e.CheckName,
                ["tool"] = e.Tool,
                ["command"] = e.Command,
                ["exitCode"] = e.ExitCode,
                ["output"] = e.OutputSnippet,
                ["passed"] = e.Passed,
                ["agentName"] = e.AgentName,
                ["createdAt"] = e.CreatedAt.ToString("O")
            }).ToList();

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["phase"] = phase?.ToString() ?? "all",
                    ["total"] = evidence.Count,
                    ["passed"] = passedCount,
                    ["failed"] = failedCount,
                    ["evidence"] = rows
                }
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
}
