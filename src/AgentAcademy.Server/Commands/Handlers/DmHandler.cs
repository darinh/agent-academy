using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles DM — sends a direct message to another agent or to the human.
/// Replaces the legacy ASK_HUMAN command with a unified messaging system.
/// </summary>
public sealed class DmHandler : ICommandHandler
{
    public string CommandName => "DM";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("recipient", out var recipientObj) || recipientObj is not string recipient ||
            string.IsNullOrWhiteSpace(recipient))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required arg: recipient. Usage: DM:\n  Recipient: @Human or @AgentName\n  Message: <your message>"
            };
        }

        if (!command.Args.TryGetValue("message", out var messageObj) || messageObj is not string message ||
            string.IsNullOrWhiteSpace(message))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required arg: message. Usage: DM:\n  Recipient: @Human or @AgentName\n  Message: <your message>"
            };
        }

        // Normalize recipient: strip leading @
        var normalizedRecipient = recipient.TrimStart('@');

        // Route to human or agent
        if (string.Equals(normalizedRecipient, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return await SendToHumanAsync(command, context, message);
        }
        else
        {
            return await SendToAgentAsync(command, context, normalizedRecipient, message);
        }
    }

    private static async Task<CommandEnvelope> SendToHumanAsync(
        CommandEnvelope command, CommandContext context, string message)
    {
        var catalog = context.Services.GetRequiredService<AgentCatalogOptions>();
        var messages = context.Services.GetRequiredService<MessageService>();
        var roomService = context.Services.GetRequiredService<RoomService>();
        var notificationManager = context.Services.GetRequiredService<NotificationManager>();

        var roomId = context.RoomId ?? "main";

        // Store the DM
        await messages.SendDirectMessageAsync(
            context.AgentId, context.AgentName ?? context.AgentId,
            context.AgentRole ?? "Agent", "human", message, roomId);

        // Route through notification bridge (Discord) for immediate delivery
        var roomName = roomId;
        try
        {
            var rooms = await roomService.GetRoomsAsync();
            var room = rooms.FirstOrDefault(r => r.Id == context.RoomId);
            if (room is not null)
                roomName = room.Name;
        }
        catch
        {
            // Fall back to roomId as name
        }

        var agentQuestion = new AgentQuestion(
            AgentId: context.AgentId,
            AgentName: context.AgentName ?? context.AgentId,
            RoomId: roomId,
            RoomName: roomName,
            Question: message
        );

        var (sent, error) = await notificationManager.SendDirectMessageDisplayAsync(agentQuestion);

        if (!sent)
        {
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["status"] = "stored",
                    ["recipient"] = "Human",
                    ["notification"] = $"DM stored but notification delivery failed: {error ?? "unknown error"}. " +
                                       "The human can still see this in their DM inbox."
                }
            };
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["status"] = "sent",
                ["recipient"] = "Human",
                ["message"] = "Your message has been sent to the human. " +
                              "The reply will appear as a direct message to you."
            }
        };
    }

    private static async Task<CommandEnvelope> SendToAgentAsync(
        CommandEnvelope command, CommandContext context, string recipientId, string message)
    {
        var catalog = context.Services.GetRequiredService<AgentCatalogOptions>();
        var messages = context.Services.GetRequiredService<MessageService>();
        var roomService = context.Services.GetRequiredService<RoomService>();

        // Validate agent exists (case-insensitive, use canonical ID)
        var agents = catalog.Agents;
        var targetAgent = agents.FirstOrDefault(
            a => string.Equals(a.Id, recipientId, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(a.Name, recipientId, StringComparison.OrdinalIgnoreCase));

        if (targetAgent is null)
        {
            var availableAgents = string.Join(", ", agents.Select(a => a.Name));
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Unknown recipient '{recipientId}'. Available agents: {availableAgents}, or use @Human."
            };
        }

        // Don't allow self-DMs
        if (string.Equals(targetAgent.Id, context.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = "Cannot send a DM to yourself."
            };
        }

        var roomId = context.RoomId ?? "main";

        // Store the DM
        await messages.SendDirectMessageAsync(
            context.AgentId, context.AgentName ?? context.AgentId,
            context.AgentRole ?? "Agent", targetAgent.Id, message, roomId);

        // Forward DM to Discord Messages category (channel message, no thread)
        var notificationManager = context.Services.GetRequiredService<NotificationManager>();
        var roomName = roomId;
        try
        {
            var rooms = await roomService.GetRoomsAsync();
            var room = rooms.FirstOrDefault(r => r.Id == context.RoomId);
            if (room is not null) roomName = room.Name;
        }
        catch { /* fall back to roomId */ }

        // Fire-and-forget — delivery failure shouldn't block the DM
        _ = notificationManager.SendDirectMessageDisplayAsync(new AgentQuestion(
            AgentId: context.AgentId,
            AgentName: context.AgentName ?? context.AgentId,
            RoomId: roomId,
            RoomName: roomName,
            Question: $"[DM to {targetAgent.Name}] {message}"
        ));

        // Trigger recipient agent to respond promptly
        var orchestrator = context.Services.GetRequiredService<AgentOrchestrator>();
        orchestrator.HandleDirectMessage(targetAgent.Id);

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["status"] = "delivered",
                ["recipient"] = targetAgent.Name,
                ["recipientId"] = targetAgent.Id,
                ["message"] = $"Your DM has been delivered to {targetAgent.Name}. " +
                              "They will respond in their next turn."
            }
        };
    }
}
