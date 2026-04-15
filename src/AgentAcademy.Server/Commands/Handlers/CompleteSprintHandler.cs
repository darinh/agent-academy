using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles COMPLETE_SPRINT — marks the active sprint as completed.
/// Must be in FinalSynthesis stage with required artifacts, unless force=true.
/// </summary>
public sealed class CompleteSprintHandler : ICommandHandler
{
    public string CommandName => "COMPLETE_SPRINT";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Optional: explicit sprintId
        string? sprintId = null;
        if (command.Args.TryGetValue("sprintId", out var sprintIdObj) && sprintIdObj is string sid
            && !string.IsNullOrWhiteSpace(sid))
        {
            sprintId = sid;
        }

        // Optional: force flag
        var force = false;
        if (command.Args.TryGetValue("force", out var forceObj))
        {
            force = forceObj switch
            {
                bool b => b,
                string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        var roomService = context.Services.GetRequiredService<IRoomService>();
        var sprintService = context.Services.GetRequiredService<SprintService>();

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
        }

        try
        {
            var sprint = await sprintService.CompleteSprintAsync(sprintId, force);
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["sprintId"] = sprint.Id,
                    ["number"] = sprint.Number,
                    ["status"] = sprint.Status,
                    ["completedAt"] = sprint.CompletedAt?.ToString("O"),
                    ["message"] = $"Sprint #{sprint.Number} completed"
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
