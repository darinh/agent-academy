using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles ROOM_TOPIC — sets or clears a room's topic description.
/// Any agent in the room can set the topic; planners and humans can set it on any room.
/// </summary>
public sealed class RoomTopicHandler : ICommandHandler
{
    public string CommandName => "ROOM_TOPIC";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("roomId", out var roomIdObj)
            && !command.Args.TryGetValue("roomid", out roomIdObj))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: roomId. Usage: ROOM_TOPIC: RoomId: <id>, Topic: <text>"
            };
        }

        var roomId = (roomIdObj as string)?.Trim();
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "roomId cannot be empty."
            };
        }

        string? topic = null;
        if (command.Args.TryGetValue("topic", out var topicObj) && topicObj is string topicStr)
            topic = topicStr;
        else if (command.Args.TryGetValue("value", out var valObj) && valObj is string valStr)
            topic = valStr;

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();
        var room = await runtime.GetRoomAsync(roomId);

        if (room is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Room '{roomId}' not found."
            };
        }

        if (room.Status == RoomStatus.Archived)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Cannot set topic on archived room '{room.Name}'."
            };
        }

        try
        {
            var updated = await runtime.SetRoomTopicAsync(roomId, topic);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["roomId"] = updated.Id,
                    ["roomName"] = updated.Name,
                    ["topic"] = updated.Topic,
                    ["message"] = updated.Topic is not null
                        ? $"Topic set for '{updated.Name}': {updated.Topic}"
                        : $"Topic cleared for '{updated.Name}'."
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
