using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles START_SPRINT — creates a new sprint for the active workspace.
/// Only one active sprint per workspace is allowed.
/// </summary>
public sealed class StartSprintHandler : ICommandHandler
{
    public string CommandName => "START_SPRINT";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();
        var sprintService = context.Services.GetRequiredService<SprintService>();

        var workspacePath = await runtime.GetActiveWorkspacePathAsync();
        if (string.IsNullOrEmpty(workspacePath))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "No active workspace. Open a workspace before starting a sprint."
            };
        }

        try
        {
            var sprint = await sprintService.CreateSprintAsync(workspacePath);
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["sprintId"] = sprint.Id,
                    ["number"] = sprint.Number,
                    ["stage"] = sprint.CurrentStage,
                    ["workspacePath"] = sprint.WorkspacePath,
                    ["overflowFrom"] = sprint.OverflowFromSprintId,
                    ["message"] = $"Sprint #{sprint.Number} started at stage {sprint.CurrentStage}"
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = ex.Message
            };
        }
    }
}
