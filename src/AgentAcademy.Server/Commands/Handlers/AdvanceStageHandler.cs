using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

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

        // force=true skips prerequisites — restricted to Human role only.
        // Agents cannot bypass task completion gates autonomously.
        var force = false;
        if (string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase)
            && command.Args.TryGetValue("force", out var forceObj))
        {
            force = forceObj switch
            {
                bool b => b,
                string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        var roomService = context.Services.GetRequiredService<IRoomService>();
        var sprintService = context.Services.GetRequiredService<ISprintService>();
        var stageService = context.Services.GetRequiredService<SprintStageService>();
        var sessionService = context.Services.GetRequiredService<ConversationSessionService>();

        // If no explicit sprintId, resolve from active workspace
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
        }

        try
        {
            var previousStage = (await sprintService.GetSprintByIdAsync(sprintId))?.CurrentStage;
            var sprint = await stageService.AdvanceStageAsync(sprintId, force);

            // If awaiting sign-off, return a specific message — don't create a new session
            if (sprint.AwaitingSignOff)
            {
                return command with
                {
                    Status = CommandStatus.Success,
                    Result = new Dictionary<string, object?>
                    {
                        ["sprintId"] = sprint.Id,
                        ["number"] = sprint.Number,
                        ["previousStage"] = previousStage,
                        ["currentStage"] = sprint.CurrentStage,
                        ["pendingStage"] = sprint.PendingStage,
                        ["awaitingSignOff"] = true,
                        ["message"] = $"Sprint #{sprint.Number} awaiting user sign-off to advance from {sprint.CurrentStage} → {sprint.PendingStage}. "
                            + "A human must approve before the stage changes."
                    }
                };
            }

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
                    ["forced"] = force,
                    ["message"] = $"Sprint #{sprint.Number} advanced: {previousStage} → {sprint.CurrentStage}"
                        + (force ? " (forced — prerequisites skipped)" : "")
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
