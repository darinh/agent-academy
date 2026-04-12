using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles ROOM_HISTORY — reads recent messages from any room without moving the agent.
/// </summary>
public sealed class RoomHistoryHandler : ICommandHandler
{
    public string CommandName => "ROOM_HISTORY";
    public bool IsRetrySafe => true;

    private const int DefaultCount = 20;
    private const int MaxCount = 50;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("roomid", out var roomIdObj) && !command.Args.TryGetValue("roomId", out roomIdObj))
            roomIdObj = null;

        if (roomIdObj is not string roomId || string.IsNullOrWhiteSpace(roomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: roomId"
            };
        }

        var count = DefaultCount;
        if (command.Args.TryGetValue("count", out var countObj))
        {
            if (countObj is string countStr && int.TryParse(countStr, out var parsed))
                count = Math.Min(parsed, MaxCount);
            else if (countObj is int countInt)
                count = Math.Min(countInt, MaxCount);
        }

        var roomService = context.Services.GetRequiredService<RoomService>();

        var room = await roomService.GetRoomAsync(roomId);
        if (room is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Room '{roomId}' not found."
            };
        }

        var messages = room.RecentMessages
            .TakeLast(count)
            .Select(m => new Dictionary<string, object?>
            {
                ["sender"] = m.SenderName,
                ["role"] = m.SenderRole ?? m.SenderKind.ToString(),
                ["content"] = m.Content.Length > 500 ? m.Content[..500] + "…" : m.Content,
                ["sentAt"] = m.SentAt.ToString("O")
            })
            .ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["roomId"] = roomId,
                ["roomName"] = room.Name,
                ["messages"] = messages,
                ["count"] = messages.Count
            }
        };
    }
}
