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
    /// Whether this command performs a destructive or irreversible action.
    /// Destructive commands require explicit <c>confirm=true</c> in args before execution.
    /// </summary>
    bool IsDestructive => false;

    /// <summary>
    /// Human-readable description of the destructive impact, shown when confirmation is required.
    /// Only meaningful when <see cref="IsDestructive"/> is true.
    /// </summary>
    string DestructiveWarning => $"{CommandName} performs a destructive action.";

    /// <summary>
    /// Whether the pipeline may automatically retry this command on transient failure.
    /// Only safe for idempotent / read-only commands. Defaults to false.
    /// </summary>
    bool IsRetrySafe => false;

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
