namespace AgentAcademy.Shared.Models;

/// <summary>
/// Envelope for a structured agent command and its result.
/// Shape is stable — new commands extend Args/Result, never change the envelope.
/// </summary>
public record CommandEnvelope(
    string Command,
    Dictionary<string, object?> Args,
    CommandStatus Status,
    Dictionary<string, object?>? Result,
    string? Error,
    string CorrelationId,
    DateTime Timestamp,
    string ExecutedBy
);

/// <summary>
/// Result of parsing an agent's text response for structured commands.
/// </summary>
public record CommandParseResult(
    List<ParsedCommand> Commands,
    string RemainingText
);

/// <summary>
/// A single command extracted from agent text, before execution.
/// </summary>
public record ParsedCommand(
    string Command,
    Dictionary<string, string> Args
);

/// <summary>
/// Defines which commands an agent is permitted to execute.
/// Supports wildcard patterns (e.g., "READ_*", "LIST_*").
/// </summary>
public record CommandPermissionSet(
    List<string> Allowed,
    List<string> Denied
);
