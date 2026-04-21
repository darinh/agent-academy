using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles INVITE_TO_ROOM — moves a specified agent to a specified room.
/// Only planners and humans can invite agents to rooms.
/// Agents currently Working in a breakout must be recalled first.
/// </summary>
public sealed class InviteToRoomHandler : ICommandHandler
{
    public string CommandName => "INVITE_TO_ROOM";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only planners and humans can invite agents to rooms"
            };
        }

        // Resolve agentId from agentId arg or value shorthand
        string? targetAgent = null;
        if (command.Args.TryGetValue("agentId", out var agentIdObj) && agentIdObj is string agentIdStr
            && !string.IsNullOrWhiteSpace(agentIdStr))
        {
            targetAgent = agentIdStr;
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
                Error = "Missing required argument: agentId (name or ID of the agent to invite)"
            };
        }

        targetAgent = targetAgent.Trim();

        // Resolve roomId
        if (!command.Args.TryGetValue("roomid", out var roomIdObj) && !command.Args.TryGetValue("roomId", out roomIdObj))
            roomIdObj = null;

        if (roomIdObj is not string roomId || string.IsNullOrWhiteSpace(roomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: roomId. Use LIST_ROOMS to see available rooms."
            };
        }

        roomId = roomId.Trim();

        var catalog = context.Services.GetRequiredService<IAgentCatalog>();
        var agentLocations = context.Services.GetRequiredService<IAgentLocationService>();
        var messages = context.Services.GetRequiredService<IMessageService>();
        var roomService = context.Services.GetRequiredService<IRoomService>();

        // Resolve agent by name or ID
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
                Error = $"Agent '{targetAgent}' not found. Use LIST_AGENTS to see available agents."
            };
        }

        // Verify room exists
        var room = await roomService.GetRoomAsync(roomId);
        if (room is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Room '{roomId}' not found. Use LIST_ROOMS to see available rooms."
            };
        }

        // Reject if room is archived
        if (room.Status == RoomStatus.Archived)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Room '{room.Name}' is archived. Use REOPEN_ROOM to reactivate it first."
            };
        }

        // Reject if agent is Working in a breakout
        var location = await agentLocations.GetAgentLocationAsync(agent.Id);
        if (location is not null && location.State == AgentState.Working
            && !string.IsNullOrEmpty(location.BreakoutRoomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Agent '{agent.Name}' is currently working in a breakout room. Use RECALL_AGENT first."
            };
        }

        // Check if agent is already in the target room
        if (location is not null && location.RoomId == roomId)
        {
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["agentId"] = agent.Id,
                    ["agentName"] = agent.Name,
                    ["roomId"] = roomId,
                    ["roomName"] = room.Name,
                    ["message"] = $"{agent.Name} is already in room '{room.Name}'."
                }
            };
        }

        // Move the agent
        await agentLocations.MoveAgentAsync(agent.Id, roomId, AgentState.Idle);

        // Post system message in target room (best-effort — move already succeeded)
        try
        {
            await messages.PostSystemStatusAsync(roomId,
                $"📨 {agent.Name} has been invited to this room by {context.AgentName}.");
        }
        catch { /* move succeeded; message is informational */ }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["agentId"] = agent.Id,
                ["agentName"] = agent.Name,
                ["roomId"] = roomId,
                ["roomName"] = room.Name,
                ["message"] = $"{agent.Name} has been invited to room '{room.Name}'."
            }
        };
    }
}
