using AgentAcademy.Server.Services;
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

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();
        var count = await runtime.CleanupStaleRoomsAsync();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["archivedCount"] = count,
                ["message"] = count > 0
                    ? $"Archived {count} stale room(s) where all tasks were complete."
                    : "No stale rooms found to clean up."
            }
        };
    }
}
