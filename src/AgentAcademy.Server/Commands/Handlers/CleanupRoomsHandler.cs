using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CLEANUP_ROOMS — scans for stale rooms where all tasks are complete
/// and archives them. Only planners and humans can trigger cleanup.
/// </summary>
public sealed class CleanupRoomsHandler : ICommandHandler
{
    public string CommandName => "CLEANUP_ROOMS";
    public bool IsDestructive => true;
    public string DestructiveWarning => "CLEANUP_ROOMS will archive all stale rooms where tasks are complete. This affects multiple rooms.";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only planners can cleanup rooms"
            };
        }

        var lifecycle = context.Services.GetRequiredService<IRoomLifecycleService>();
        var result = await lifecycle.CleanupStaleRoomsDetailedAsync();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["archivedCount"] = result.ArchivedCount,
                ["skippedCount"] = result.SkippedCount,
                ["perRoomSkipReasons"] = result.Skips.Select(s => new Dictionary<string, object?>
                {
                    ["roomId"] = s.RoomId,
                    ["roomName"] = s.RoomName,
                    ["reason"] = s.ReasonWireValue,
                }).ToList(),
                ["message"] = result.ArchivedCount > 0
                    ? $"Archived {result.ArchivedCount} stale room(s) where all tasks were complete; held back {result.SkippedCount} candidate(s)."
                    : result.SkippedCount > 0
                        ? $"No stale rooms archived; held back {result.SkippedCount} candidate(s) — see perRoomSkipReasons."
                        : "No stale rooms found to clean up."
            }
        };
    }
}
