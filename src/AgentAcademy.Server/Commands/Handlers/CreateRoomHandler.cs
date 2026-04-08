using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CREATE_ROOM — creates a new persistent collaboration room as a work context.
/// Only planners can create rooms.
/// </summary>
public sealed class CreateRoomHandler : ICommandHandler
{
    public string CommandName => "CREATE_ROOM";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!string.Equals(context.AgentRole, "Planner", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Only planners can create collaboration rooms"
            };
        }

        if (!command.Args.TryGetValue("name", out var nameObj) || nameObj is not string name
            || string.IsNullOrWhiteSpace(name))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Missing required argument: name. Example: CREATE_ROOM: name=\"Feature: User Profiles\""
            };
        }

        var description = command.Args.TryGetValue("description", out var descObj) && descObj is string desc
            ? desc : null;

        var runtime = context.Services.GetRequiredService<WorkspaceRuntime>();
        var room = await runtime.CreateRoomAsync(name.Trim(), description?.Trim());

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["roomId"] = room.Id,
                ["roomName"] = room.Name,
                ["status"] = room.Status.ToString(),
                ["message"] = $"Room '{room.Name}' created. Use MOVE_TO_ROOM to enter it."
            }
        };
    }
}
