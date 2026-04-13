using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CREATE_TASK_ITEM — creates a work item within a room, assigned to the calling agent
/// or a specified agent.
/// </summary>
public sealed class CreateTaskItemHandler : ICommandHandler
{
    public string CommandName => "CREATE_TASK_ITEM";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("title", out var titleObj) || titleObj is not string title
            || string.IsNullOrWhiteSpace(title))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: title"
            };
        }

        var description = command.Args.TryGetValue("description", out var descObj)
            && descObj is string desc ? desc : string.Empty;

        var assignedToRaw = command.Args.TryGetValue("assignedTo", out var assignObj)
            && assignObj is string assign && !string.IsNullOrWhiteSpace(assign)
            ? assign : context.AgentId;

        var roomId = command.Args.TryGetValue("roomId", out var roomObj)
            && roomObj is string room && !string.IsNullOrWhiteSpace(room)
            ? room : context.RoomId;

        if (string.IsNullOrWhiteSpace(roomId))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "No roomId specified and agent has no current room"
            };
        }

        string? breakoutRoomId = command.Args.TryGetValue("breakoutRoomId", out var brObj)
            && brObj is string br && !string.IsNullOrWhiteSpace(br)
            ? br : context.BreakoutRoomId;

        var catalog = context.Services.GetRequiredService<IAgentCatalog>();
        var roomService = context.Services.GetRequiredService<RoomService>();
        var taskItems = context.Services.GetRequiredService<TaskItemService>();

        // Resolve assignee: accept agent ID or name, normalize to ID
        var agents = catalog.Agents;
        var resolvedAgent = agents.FirstOrDefault(a =>
            string.Equals(a.Id, assignedToRaw, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Name, assignedToRaw, StringComparison.OrdinalIgnoreCase));

        if (resolvedAgent is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Agent '{assignedToRaw}' not found in catalog"
            };
        }

        var assignedTo = resolvedAgent.Id;

        // Validate room exists
        var roomSnapshot = await roomService.GetRoomAsync(roomId);
        if (roomSnapshot is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Room '{roomId}' not found"
            };
        }

        try
        {
            var item = await taskItems.CreateTaskItemAsync(
                title, description, assignedTo, roomId, breakoutRoomId);

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["taskItemId"] = item.Id,
                    ["title"] = item.Title,
                    ["status"] = item.Status.ToString(),
                    ["assignedTo"] = item.AssignedTo,
                    ["roomId"] = item.RoomId,
                    ["message"] = $"Task item '{title}' created"
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
