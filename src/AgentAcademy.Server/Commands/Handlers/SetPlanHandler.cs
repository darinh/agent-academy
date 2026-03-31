using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SET_PLAN — writes markdown plan content to the current room context.
/// </summary>
public sealed class SetPlanHandler : ICommandHandler
{
    public string CommandName => "SET_PLAN";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!SetPlanCommand.TryParse(command.Args, out var parsed, out var error))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = error
            };
        }

        var roomId = context.BreakoutRoomId ?? context.RoomId;
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = "SET_PLAN requires an active room context."
            };
        }

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();

        try
        {
            await runtime.SetPlanAsync(roomId, parsed!.Content);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["roomId"] = roomId,
                    ["contentLength"] = parsed.Content.Length,
                    ["message"] = $"Plan saved for room '{roomId}'."
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = ex.Message
            };
        }
    }
}
