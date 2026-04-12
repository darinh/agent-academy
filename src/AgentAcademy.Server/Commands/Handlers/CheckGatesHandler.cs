using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CHECK_GATES — checks whether a task meets minimum evidence
/// requirements for the next phase transition.
/// Any agent can check gates (read-only operation).
/// </summary>
public sealed class CheckGatesHandler : ICommandHandler
{
    public string CommandName => "CHECK_GATES";
    public bool IsRetrySafe => true;

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

        var taskEvidence = context.Services.GetRequiredService<TaskEvidenceService>();

        try
        {
            var result = await taskEvidence.CheckGatesAsync(taskId);

            var evidenceSummary = result.Evidence
                .Select(e => new Dictionary<string, object?>
                {
                    ["phase"] = e.Phase.ToString(),
                    ["checkName"] = e.CheckName,
                    ["passed"] = e.Passed,
                    ["agentName"] = e.AgentName
                }).ToList();

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskId"] = result.TaskId,
                    ["currentPhase"] = result.CurrentPhase,
                    ["targetPhase"] = result.TargetPhase,
                    ["met"] = result.Met,
                    ["requiredChecks"] = result.RequiredChecks,
                    ["passedChecks"] = result.PassedChecks,
                    ["missingChecks"] = result.MissingChecks,
                    ["evidence"] = evidenceSummary,
                    ["message"] = result.Met
                        ? $"✅ Gates met for {result.CurrentPhase} → {result.TargetPhase} ({result.PassedChecks}/{result.RequiredChecks} checks passed)"
                        : $"❌ Gates NOT met for {result.CurrentPhase} → {result.TargetPhase} ({result.PassedChecks}/{result.RequiredChecks} checks passed). Missing: {string.Join(", ", result.MissingChecks)}"
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
