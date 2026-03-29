using AgentAcademy.Server.Notifications;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles ASK_HUMAN — sends a question to the human via Discord (or other notification provider).
/// The human's reply is routed back to the agent's room asynchronously.
/// </summary>
public sealed class AskHumanHandler : ICommandHandler
{
    public string CommandName => "ASK_HUMAN";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Extract the question from args
        if (!command.Args.TryGetValue("question", out var questionObj) || questionObj is not string question ||
            string.IsNullOrWhiteSpace(question))
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = "Missing required arg: question. Usage: ASK_HUMAN:\n  Question: <your question>"
            };
        }

        var notificationManager = context.Services.GetRequiredService<NotificationManager>();

        // Resolve room name from WorkspaceRuntime for Discord category naming
        var roomName = context.RoomId ?? "general";
        try
        {
            var runtime = context.Services.GetRequiredService<Services.WorkspaceRuntime>();
            var rooms = await runtime.GetRoomsAsync();
            var room = rooms.FirstOrDefault(r => r.Id == context.RoomId);
            if (room is not null)
                roomName = room.Name;
        }
        catch
        {
            // Fall back to roomId as name — non-critical
        }

        var agentQuestion = new AgentQuestion(
            AgentId: context.AgentId,
            AgentName: context.AgentName ?? context.AgentId,
            RoomId: context.RoomId ?? "main",
            RoomName: roomName,
            Question: question
        );

        var sent = await notificationManager.SendAgentQuestionAsync(agentQuestion);

        if (!sent)
        {
            return command with
            {
                Status = CommandStatus.Error,
                Error = "No notification provider is connected that supports agent questions. " +
                        "Ensure Discord is configured and connected in Settings."
            };
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["status"] = "sent",
                ["agentName"] = agentQuestion.AgentName,
                ["roomName"] = agentQuestion.RoomName,
                ["message"] = "Your question has been sent to the human via Discord. " +
                              "The reply will appear as a message in this room."
            }
        };
    }
}
