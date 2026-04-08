using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles ADVANCE_STAGE — moves the active sprint to the next stage.
/// Validates artifact gates, then creates a new conversation session for the new stage.
/// </summary>
public sealed class AdvanceStageHandler : ICommandHandler
{
    public string CommandName => "ADVANCE_STAGE";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Accept sprintId as explicit arg, or resolve from active workspace
        string? sprintId = null;
        if (command.Args.TryGetValue("sprintId", out var sprintIdObj) && sprintIdObj is string sid
            && !string.IsNullOrWhiteSpace(sid))
        {
            sprintId = sid;
        }

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();
        var sprintService = context.Services.GetRequiredService<SprintService>();
        var sessionService = context.Services.GetRequiredService<ConversationSessionService>();

        // If no explicit sprintId, resolve from active workspace
        if (string.IsNullOrEmpty(sprintId))
        {
            var workspacePath = await runtime.GetActiveWorkspacePathAsync();
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
        }

        try
        {
            var previousStage = (await sprintService.GetSprintByIdAsync(sprintId))?.CurrentStage;
            var sprint = await sprintService.AdvanceStageAsync(sprintId);

            // Create a new conversation session for the new stage.
            // Non-fatal: stage advancement succeeds even if session creation fails
            // (matches graceful degradation pattern used throughout sprint context loading).
            string? sessionWarning = null;
            if (!string.IsNullOrEmpty(context.RoomId))
            {
                try
                {
                    await sessionService.CreateSessionForStageAsync(
                        context.RoomId, sprint.Id, sprint.CurrentStage);
                }
                catch (Exception)
                {
                    sessionWarning = "Stage advanced but new conversation session could not be created.";
                }
            }

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["sprintId"] = sprint.Id,
                    ["number"] = sprint.Number,
                    ["previousStage"] = previousStage,
                    ["currentStage"] = sprint.CurrentStage,
                    ["warning"] = sessionWarning,
                    ["message"] = $"Sprint #{sprint.Number} advanced: {previousStage} → {sprint.CurrentStage}"
                        + (sessionWarning is not null ? $" ⚠️ {sessionWarning}" : "")
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
