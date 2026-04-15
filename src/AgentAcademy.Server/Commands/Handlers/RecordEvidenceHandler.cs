using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RECORD_EVIDENCE — records a structured verification check against a task.
/// The calling agent must be the task assignee, a reviewer, or a Planner.
/// </summary>
public sealed class RecordEvidenceHandler : ICommandHandler
{
    public string CommandName => "RECORD_EVIDENCE";

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

        if (!command.Args.TryGetValue("checkName", out var checkNameObj) || checkNameObj is not string checkName
            || string.IsNullOrWhiteSpace(checkName))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: checkName (e.g. 'build', 'tests', 'type-check', 'code-review')"
            };
        }

        if (!command.Args.TryGetValue("passed", out var passedObj) || passedObj is not string passedStr)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: passed (true or false)"
            };
        }

        if (!bool.TryParse(passedStr, out var passed))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = $"Invalid value for passed: '{passedStr}'. Must be 'true' or 'false'"
            };
        }

        // Parse phase (default: After)
        var phase = EvidencePhase.After;
        if (command.Args.TryGetValue("phase", out var phaseObj) && phaseObj is string phaseStr
            && !string.IsNullOrWhiteSpace(phaseStr))
        {
            if (!Enum.TryParse(phaseStr, ignoreCase: true, out phase))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = $"Invalid phase: '{phaseStr}'. Valid: Baseline, After, Review"
                };
            }
        }

        // Parse optional fields
        var tool = "manual";
        if (command.Args.TryGetValue("tool", out var toolObj) && toolObj is string toolStr
            && !string.IsNullOrWhiteSpace(toolStr))
            tool = toolStr;

        string? cmd = null;
        if (command.Args.TryGetValue("command", out var cmdObj) && cmdObj is string cmdStr)
            cmd = cmdStr;

        int? exitCode = null;
        if (command.Args.TryGetValue("exitCode", out var exitObj) && exitObj is string exitStr
            && int.TryParse(exitStr, out var ec))
            exitCode = ec;

        string? outputSnippet = null;
        if (command.Args.TryGetValue("output", out var outputObj) && outputObj is string outputStr)
            outputSnippet = outputStr;

        var taskEvidence = context.Services.GetRequiredService<ITaskEvidenceService>();
        var taskQueries = context.Services.GetRequiredService<ITaskQueryService>();

        try
        {
            // Validate agent can record evidence on this task
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

            var isAssignee = string.Equals(task.AssignedAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase);
            var isReviewer = string.Equals(task.ReviewerAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase);
            var isPlanner = string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase);
            var isHuman = string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase);

            if (!isAssignee && !isReviewer && !isPlanner && !isHuman)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Permission,
                    Error = $"Only the assignee, reviewer, or planner can record evidence on task '{taskId}'"
                };
            }

            var evidence = await taskEvidence.RecordEvidenceAsync(
                taskId, context.AgentId, context.AgentName,
                phase, checkName, tool, cmd, exitCode, outputSnippet, passed);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["evidenceId"] = evidence.Id,
                    ["taskId"] = evidence.TaskId,
                    ["phase"] = evidence.Phase.ToString(),
                    ["checkName"] = evidence.CheckName,
                    ["passed"] = evidence.Passed,
                    ["message"] = $"Evidence recorded: {checkName} ({phase}) — {(passed ? "passed" : "FAILED")}"
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
