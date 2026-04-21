using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_ACTIVE_CONNECTIONS — returns information about active
/// SignalR connections for the current server instance.
/// Restricted to Planner, Reviewer, and Human roles.
/// </summary>
public sealed class ShowActiveConnectionsHandler : ICommandHandler
{
    public string CommandName => "SHOW_ACTIVE_CONNECTIONS";
    public bool IsRetrySafe => true;

    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Planner", "Reviewer", "Human"
    };

    public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // In-handler role gate
        if (!AllowedRoles.Contains(context.AgentRole))
        {
            return Task.FromResult(command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Permission,
                Error = $"SHOW_ACTIVE_CONNECTIONS is restricted to Planner, Reviewer, and Human roles. Your role: {context.AgentRole}"
            });
        }

        var tracker = context.Services.GetService<SignalRConnectionTracker>();
        if (tracker is null)
        {
            return Task.FromResult(command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Connection tracker is not available."
            });
        }

        var connections = tracker.GetConnections();
        var now = DateTime.UtcNow;

        return Task.FromResult(command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["count"] = connections.Count,
                ["instance"] = Environment.MachineName,
                ["connections"] = connections.Select(c => new Dictionary<string, object?>
                {
                    ["connectionId"] = c.ConnectionId[..Math.Min(8, c.ConnectionId.Length)] + "…",
                    ["connectedAt"] = c.ConnectedAt.ToString("O"),
                    ["duration"] = FormatDuration(now - c.ConnectedAt)
                }).ToList()
            }
        });
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{(int)duration.TotalSeconds}s";
    }
}
