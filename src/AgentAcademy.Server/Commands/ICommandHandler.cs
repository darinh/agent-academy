using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands;

/// <summary>
/// Interface for command handlers. Each handler processes one command type.
/// </summary>
public interface ICommandHandler
{
    /// <summary>
    /// The SCREAMING_SNAKE command name this handler processes.
    /// </summary>
    string CommandName { get; }

    /// <summary>
    /// Execute the command and return the completed envelope.
    /// </summary>
    Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context);
}

/// <summary>
/// Contextual information available to command handlers during execution.
/// </summary>
public record CommandContext(
    string AgentId,
    string AgentName,
    string AgentRole,
    string? RoomId,
    string? BreakoutRoomId,
    IServiceProvider Services,
    AgentGitIdentity? GitIdentity = null,
    string? WorkingDirectory = null
);
