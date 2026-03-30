using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles MOVE_TO_ROOM — moves the calling agent to a different room.
/// </summary>
public sealed class MoveToRoomHandler : ICommandHandler
{
    public string CommandName => "MOVE_TO_ROOM";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("roomid", out var roomIdObj) && !command.Args.TryGetValue("roomId", out roomIdObj))
            roomIdObj = null;

        if (roomIdObj is not string roomId || string.IsNullOrWhiteSpace(roomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = "Missing required argument: roomId. Use LIST_ROOMS to see available rooms."
            };
        }

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();

        // Verify room exists
        var room = await runtime.GetRoomAsync(roomId);
        if (room is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = $"Room '{roomId}' not found. Use LIST_ROOMS to see available rooms."
            };
        }

        // Move the agent
        await runtime.MoveAgentAsync(context.AgentId, roomId, AgentState.Idle);

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["roomId"] = roomId,
                ["roomName"] = room.Name,
                ["message"] = $"Moved to room '{room.Name}'."
            }
        };
    }
}
