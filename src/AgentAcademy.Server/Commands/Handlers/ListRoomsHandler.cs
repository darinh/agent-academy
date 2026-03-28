using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_ROOMS — returns all rooms with status, participants, and message counts.
/// </summary>
public sealed class ListRoomsHandler : ICommandHandler
{
    public string CommandName => "LIST_ROOMS";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();
        var rooms = await runtime.GetRoomsAsync();

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
