using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_AGENTS — returns all agents with location, state, and active task info.
/// </summary>
public sealed class ListAgentsHandler : ICommandHandler
{
    public string CommandName => "LIST_AGENTS";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var catalog = context.Services.GetRequiredService<AgentCatalogOptions>();
        var agentLocations = context.Services.GetRequiredService<AgentLocationService>();
        var agents = catalog.Agents;
        var locations = await agentLocations.GetAgentLocationsAsync();
        var locationMap = locations.ToDictionary(l => l.AgentId);

        var result = agents.Select(a =>
        {
            locationMap.TryGetValue(a.Id, out var location);
            return new Dictionary<string, object?>
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["role"] = a.Role,
                ["roomId"] = location?.RoomId,
                ["state"] = location?.State.ToString() ?? "Unknown",
                ["breakoutRoomId"] = location?.BreakoutRoomId
            };
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?> { ["agents"] = result, ["count"] = result.Count }
        };
    }
}
