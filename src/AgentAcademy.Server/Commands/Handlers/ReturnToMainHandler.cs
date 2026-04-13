using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RETURN_TO_MAIN — moves the calling agent back to the main collaboration room.
/// Syntactic sugar for MOVE_TO_ROOM with the default room ID.
/// </summary>
public sealed class ReturnToMainHandler : ICommandHandler
{
    public string CommandName => "RETURN_TO_MAIN";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var catalog = context.Services.GetRequiredService<IAgentCatalog>();
        var agentLocations = context.Services.GetRequiredService<AgentLocationService>();
        var roomService = context.Services.GetRequiredService<RoomService>();
        var mainRoomId = catalog.DefaultRoomId;

        var room = await roomService.GetRoomAsync(mainRoomId);
        if (room is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = "Main collaboration room not found"
            };
        }

        // Check if already there
        var location = await agentLocations.GetAgentLocationAsync(context.AgentId);
        if (location is not null && location.RoomId == mainRoomId
            && location.State != AgentState.Working)
        {
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["roomId"] = mainRoomId,
                    ["roomName"] = room.Name,
                    ["message"] = $"Already in the main room '{room.Name}'."
                }
            };
        }

        await agentLocations.MoveAgentAsync(context.AgentId, mainRoomId, AgentState.Idle);

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["roomId"] = mainRoomId,
                ["roomName"] = room.Name,
                ["message"] = $"Returned to main room '{room.Name}'."
            }
        };
    }
}
