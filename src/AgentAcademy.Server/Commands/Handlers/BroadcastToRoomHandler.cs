using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles BROADCAST_TO_ROOM — posts a message to a room on behalf of the
/// calling agent, even if the agent is not currently in that room. Useful for
/// planners announcing decisions to breakout rooms or agents sending status
/// updates to the main room from a breakout.
/// </summary>
public sealed class BroadcastToRoomHandler : ICommandHandler
{
    public string CommandName => "BROADCAST_TO_ROOM";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("roomId", out var roomIdObj) || roomIdObj is not string roomId ||
            string.IsNullOrWhiteSpace(roomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required arg: roomId. Usage: BROADCAST_TO_ROOM:\n  RoomId: <room-id>\n  Message: <your message>"
            };
        }

        if (!command.Args.TryGetValue("message", out var messageObj) || messageObj is not string message ||
            string.IsNullOrWhiteSpace(message))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required arg: message. Usage: BROADCAST_TO_ROOM:\n  RoomId: <room-id>\n  Message: <your message>"
            };
        }

        var roomService = context.Services.GetRequiredService<IRoomService>();
        var room = await roomService.GetRoomAsync(roomId);

        if (room is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Room '{roomId}' not found"
            };
        }

        if (room.Status == RoomStatus.Archived)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Room '{room.Name}' is archived — cannot broadcast to archived rooms"
            };
        }

        var messageService = context.Services.GetRequiredService<IMessageService>();
        var senderName = context.AgentName ?? context.AgentId;
        var senderRole = context.AgentRole ?? "Agent";
        var broadcastContent = $"[Broadcast from {senderName} / {senderRole}] {message}";

        await messageService.PostMessageAsync(new PostMessageRequest(
            RoomId: roomId,
            SenderId: context.AgentId,
            Content: broadcastContent,
            Kind: MessageKind.Response));

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["roomId"] = room.Id,
                ["roomName"] = room.Name,
                ["participantCount"] = room.Participants.Count,
                ["message"] = $"Broadcast delivered to room '{room.Name}' ({room.Participants.Count} participants)"
            }
        };
    }
}
