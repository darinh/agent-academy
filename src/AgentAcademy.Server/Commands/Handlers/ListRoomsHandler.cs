using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_ROOMS — returns all rooms with status, participants, and message counts.
/// Supports optional status filter (e.g., status=Active, status=Archived).
/// </summary>
public sealed class ListRoomsHandler : ICommandHandler
{
    public string CommandName => "LIST_ROOMS";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var roomService = context.Services.GetRequiredService<RoomService>();

        // When filtering for Archived rooms, we need to include them in the query
        var wantsArchived = command.Args.TryGetValue("status", out var statusObj) && statusObj is string s
            && s.Equals("Archived", StringComparison.OrdinalIgnoreCase);
        var rooms = await roomService.GetRoomsAsync(includeArchived: wantsArchived);

        // Optional status filter
        if (command.Args.TryGetValue("status", out statusObj) && statusObj is string statusFilter
            && !string.IsNullOrWhiteSpace(statusFilter))
        {
            if (!Enum.TryParse<RoomStatus>(statusFilter, ignoreCase: true, out var parsedStatus))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = $"Invalid status filter: '{statusFilter}'. Valid values: {string.Join(", ", Enum.GetNames<RoomStatus>())}"
                };
            }
            rooms = rooms.Where(r => r.Status == parsedStatus).ToList();
        }

        var result = rooms.Select(r => new Dictionary<string, object?>
        {
            ["id"] = r.Id,
            ["name"] = r.Name,
            ["status"] = r.Status.ToString(),
            ["phase"] = r.CurrentPhase.ToString(),
            ["participantCount"] = r.Participants.Count,
            ["messageCount"] = r.RecentMessages.Count,
            ["activeTask"] = r.ActiveTask?.Title
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["rooms"] = result,
                ["count"] = result.Count
            }
        };
    }
}
