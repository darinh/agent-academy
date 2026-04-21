using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles WHOAMI — returns the calling agent's identity, role, current location,
/// and available commands. Useful for agent orientation after context loss.
/// </summary>
public sealed class WhoAmIHandler : ICommandHandler
{
    public string CommandName => "WHOAMI";
    public bool IsRetrySafe => true;

    public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Look up agent definition from catalog for permissions/tools
        var catalog = context.Services.GetRequiredService<IAgentCatalog>();
        var agentDef = catalog.Agents
            .FirstOrDefault(a => string.Equals(a.Id, context.AgentId, StringComparison.OrdinalIgnoreCase));

        var result = new Dictionary<string, object?>
        {
            ["agentId"] = context.AgentId,
            ["agentName"] = context.AgentName,
            ["role"] = context.AgentRole,
            ["currentRoomId"] = context.RoomId,
            ["breakoutRoomId"] = context.BreakoutRoomId,
            ["workingDirectory"] = context.WorkingDirectory,
        };

        if (agentDef is not null)
        {
            result["enabledTools"] = agentDef.EnabledTools;
            result["capabilityTags"] = agentDef.CapabilityTags;

            if (agentDef.Permissions is not null)
            {
                result["allowedCommands"] = agentDef.Permissions.Allowed;
                result["deniedCommands"] = agentDef.Permissions.Denied;
            }
        }

        result["message"] = $"You are {context.AgentName} ({context.AgentRole}), located in room '{context.RoomId}'";

        return Task.FromResult(command with
        {
            Status = CommandStatus.Success,
            Result = result
        });
    }
}
