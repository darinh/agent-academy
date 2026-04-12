using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles REOPEN_ROOM — restores an archived room to active status for continued work.
/// Only planners can reopen rooms.
/// </summary>
public sealed class ReopenRoomHandler : ICommandHandler
{
    public string CommandName => "REOPEN_ROOM";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only planners can reopen rooms"
            };
        }

        if (!TryGetRoomId(command.Args, out var roomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: roomId. Use LIST_ROOMS to see archived rooms."
            };
        }

        var roomService = context.Services.GetRequiredService<RoomService>();
        var lifecycle = context.Services.GetRequiredService<RoomLifecycleService>();
        var room = await roomService.GetRoomAsync(roomId);

        if (room is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Room '{roomId}' not found. Use LIST_ROOMS to see available rooms."
            };
        }

        if (room.Status != RoomStatus.Archived)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Room '{room.Name}' is not archived (status: {room.Status}). Only archived rooms can be reopened."
            };
        }

        try
        {
            await lifecycle.ReopenRoomAsync(roomId);
            var reopened = (await roomService.GetRoomAsync(roomId))!;

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["roomId"] = reopened.Id,
                    ["roomName"] = reopened.Name,
                    ["status"] = reopened.Status.ToString(),
                    ["message"] = $"Room '{reopened.Name}' reopened. Use MOVE_TO_ROOM to enter it."
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
