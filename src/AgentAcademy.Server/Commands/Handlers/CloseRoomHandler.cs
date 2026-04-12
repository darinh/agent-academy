using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CLOSE_ROOM — archives a non-main collaboration room when it is empty.
/// Only planners can close rooms.
/// </summary>
public sealed class CloseRoomHandler : ICommandHandler
{
    public string CommandName => "CLOSE_ROOM";
    public bool IsDestructive => true;
    public string DestructiveWarning => "CLOSE_ROOM will archive this room permanently. Agents in the room will be moved out.";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only planners can close collaboration rooms"
            };
        }

        if (!TryGetRoomId(command.Args, out var roomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: roomId. Use LIST_ROOMS to see available rooms."
            };
        }

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();
        var room = await runtime.GetRoomAsync(roomId);
        if (room is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Room '{roomId}' not found. Use LIST_ROOMS to see available rooms."
            };
        }

        if (await runtime.IsMainCollaborationRoomAsync(roomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Room '{room.Name}' is the main collaboration room and cannot be closed."
            };
        }

        if (room.Participants.Count > 0)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Room '{room.Name}' has {room.Participants.Count} active participant(s) and cannot be closed."
            };
        }

        await runtime.CloseRoomAsync(roomId);

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["roomId"] = room.Id,
                ["roomName"] = room.Name,
                ["status"] = nameof(RoomStatus.Archived),
                ["message"] = room.Status == RoomStatus.Archived
                    ? $"Room '{room.Name}' was already archived."
                    : $"Room '{room.Name}' archived."
            }
        };
    }

    private static bool TryGetRoomId(IReadOnlyDictionary<string, object?> args, out string roomId)
    {
        roomId = string.Empty;

        if (args.TryGetValue("roomId", out var roomIdObj) && roomIdObj is string namedRoomId
            && !string.IsNullOrWhiteSpace(namedRoomId))
        {
            roomId = namedRoomId.Trim();
            return true;
        }

        if (args.TryGetValue("roomid", out roomIdObj) && roomIdObj is string lowerRoomId
            && !string.IsNullOrWhiteSpace(lowerRoomId))
        {
            roomId = lowerRoomId.Trim();
            return true;
        }

        if (args.TryGetValue("value", out var valueObj) && valueObj is string value
            && !string.IsNullOrWhiteSpace(value))
        {
            roomId = value.Trim();
            return true;
        }

        return false;
    }
}
