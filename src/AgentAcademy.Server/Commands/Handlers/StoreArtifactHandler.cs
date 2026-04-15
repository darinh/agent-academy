using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles STORE_ARTIFACT — stores a deliverable artifact for a sprint stage.
/// Upserts: if the same type already exists for the stage, it is updated.
/// </summary>
public sealed class StoreArtifactHandler : ICommandHandler
{
    public string CommandName => "STORE_ARTIFACT";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Required args: type, content
        if (!command.Args.TryGetValue("type", out var typeObj) || typeObj is not string artifactType
            || string.IsNullOrWhiteSpace(artifactType))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: type (e.g., RequirementsDocument, SprintPlan)"
            };
        }

        if (!command.Args.TryGetValue("content", out var contentObj) || contentObj is not string content
            || string.IsNullOrWhiteSpace(content))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: content"
            };
        }

        // Optional: explicit sprintId and stage
        string? sprintId = null;
        if (command.Args.TryGetValue("sprintId", out var sprintIdObj) && sprintIdObj is string sid
            && !string.IsNullOrWhiteSpace(sid))
        {
            sprintId = sid;
        }

        string? stage = null;
        if (command.Args.TryGetValue("stage", out var stageObj) && stageObj is string stg
            && !string.IsNullOrWhiteSpace(stg))
        {
            stage = stg;
        }

        var roomService = context.Services.GetRequiredService<IRoomService>();
        var sprintService = context.Services.GetRequiredService<ISprintService>();
        var artifactService = context.Services.GetRequiredService<SprintArtifactService>();

        // Resolve sprint if not explicitly given
        if (string.IsNullOrEmpty(sprintId))
        {
            var workspacePath = await roomService.GetActiveWorkspacePathAsync();
            if (string.IsNullOrEmpty(workspacePath))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "No active workspace and no sprintId provided."
                };
            }

            var activeSprint = await sprintService.GetActiveSprintAsync(workspacePath);
            if (activeSprint is null)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.NotFound,
                    Error = "No active sprint in the current workspace."
                };
            }

            sprintId = activeSprint.Id;
            stage ??= activeSprint.CurrentStage;
        }

        // If stage still not resolved, look it up from the sprint
        if (string.IsNullOrEmpty(stage))
        {
            var sprint = await sprintService.GetSprintByIdAsync(sprintId);
            if (sprint is null)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.NotFound,
                    Error = $"Sprint {sprintId} not found."
                };
            }
            stage = sprint.CurrentStage;
        }

        try
        {
            var artifact = await artifactService.StoreArtifactAsync(
                sprintId, stage, artifactType, content, context.AgentId);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["sprintId"] = sprintId,
                    ["stage"] = stage,
                    ["type"] = artifactType,
                    ["agentId"] = context.AgentId,
                    ["message"] = $"Artifact '{artifactType}' stored for stage {stage}"
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
        catch (ArgumentException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = ex.Message
            };
        }
    }
}
