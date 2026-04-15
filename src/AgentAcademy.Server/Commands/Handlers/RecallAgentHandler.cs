using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RECALL_AGENT — pulls an agent back from their breakout room.
/// Only planners can recall agents. The breakout room is closed and the agent
/// returns to idle in the parent room.
/// </summary>
public sealed class RecallAgentHandler : ICommandHandler
{
    public string CommandName => "RECALL_AGENT";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Only planners can recall agents
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only planners can recall agents from breakout rooms"
            };
        }

        // Accept agentId or value (shorthand: RECALL_AGENT: Hephaestus)
        string? targetAgent = null;
        if (command.Args.TryGetValue("agentId", out var agentIdObj) && agentIdObj is string agentId
            && !string.IsNullOrWhiteSpace(agentId))
        {
            targetAgent = agentId;
        }
        else if (command.Args.TryGetValue("value", out var valueObj) && valueObj is string valueStr
            && !string.IsNullOrWhiteSpace(valueStr))
        {
            targetAgent = valueStr;
        }

        if (string.IsNullOrWhiteSpace(targetAgent))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: AgentId (name or ID of the agent to recall)"
            };
        }

        var catalog = context.Services.GetRequiredService<IAgentCatalog>();
        var agentLocations = context.Services.GetRequiredService<AgentLocationService>();
        var breakouts = context.Services.GetRequiredService<IBreakoutRoomService>();
        var messages = context.Services.GetRequiredService<IMessageService>();

        // Resolve the agent by name or ID
        var allAgents = catalog.Agents;
        var agent = allAgents.FirstOrDefault(a =>
            a.Name.Equals(targetAgent, StringComparison.OrdinalIgnoreCase) ||
            a.Id.Equals(targetAgent, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Agent '{targetAgent}' not found"
            };
        }

        // Check the agent is actually in a breakout room
        var location = await agentLocations.GetAgentLocationAsync(agent.Id);
        if (location is null || location.State != AgentState.Working || string.IsNullOrEmpty(location.BreakoutRoomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Agent '{agent.Name}' is not currently in a breakout room"
            };
        }

        try
        {
            var breakoutId = location.BreakoutRoomId;
            var breakout = await breakouts.GetBreakoutRoomAsync(breakoutId);
            var parentRoomId = breakout?.ParentRoomId ?? context.RoomId ?? "main";

            // Post recall notices
            await messages.PostBreakoutMessageAsync(
                breakoutId, "system", "LocalAgentHost", "System",
                $"⏎ {agent.Name} has been recalled by {context.AgentName}.");

            // Close the breakout room (moves agent to idle in parent room)
            await breakouts.CloseBreakoutRoomAsync(breakoutId, BreakoutRoomCloseReason.Recalled);

            await messages.PostSystemStatusAsync(parentRoomId,
                $"⏎ {agent.Name} has been recalled from breakout by {context.AgentName} and returned to this room.");

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["agentId"] = agent.Id,
                    ["agentName"] = agent.Name,
                    ["fromBreakout"] = breakoutId,
                    ["toRoom"] = parentRoomId,
                    ["message"] = $"{agent.Name} recalled from breakout room"
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Infer(ex.Message),
                Error = ex.Message
            };
        }
    }
}
