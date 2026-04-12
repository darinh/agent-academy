using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_TASK_ITEMS — returns task items, optionally filtered by room or status.
/// Any agent can list task items.
/// </summary>
public sealed class ListTaskItemsHandler : ICommandHandler
{
    public string CommandName => "LIST_TASK_ITEMS";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        string? roomId = command.Args.TryGetValue("roomId", out var roomObj)
            && roomObj is string room && !string.IsNullOrWhiteSpace(room) ? room : null;

        TaskItemStatus? statusFilter = null;
        if (command.Args.TryGetValue("status", out var statusObj) && statusObj is string statusStr
            && !string.IsNullOrWhiteSpace(statusStr))
        {
            if (!Enum.TryParse<TaskItemStatus>(statusStr, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = $"Invalid status filter: '{statusStr}'. Valid values: Pending, Active, Done, Rejected"
                };
            }
            statusFilter = parsed;
        }

        var taskItems = context.Services.GetRequiredService<TaskItemService>();
        var items = await taskItems.GetTaskItemsAsync(roomId, statusFilter);

        var itemsList = items.Select(i => new Dictionary<string, object?>
        {
            ["id"] = i.Id,
            ["title"] = i.Title,
            ["description"] = i.Description,
            ["status"] = i.Status.ToString(),
            ["assignedTo"] = i.AssignedTo,
            ["roomId"] = i.RoomId,
            ["breakoutRoomId"] = i.BreakoutRoomId,
            ["evidence"] = i.Evidence,
            ["createdAt"] = i.CreatedAt.ToString("o")
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["items"] = itemsList,
                ["count"] = itemsList.Count,
                ["filters"] = new Dictionary<string, object?>
                {
                    ["roomId"] = roomId,
                    ["status"] = statusFilter?.ToString()
                }
            }
        };
    }
}
