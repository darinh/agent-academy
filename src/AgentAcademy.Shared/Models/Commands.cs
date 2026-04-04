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
)
{
    /// <summary>
    /// Structured error category for programmatic error handling by agents.
    /// Null when Status is Success. See <see cref="CommandErrorCode"/> for known values.
    /// </summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Well-known error codes for structured command failure categorization.
/// Agents can branch on these codes instead of parsing error message strings.
/// </summary>
public static class CommandErrorCode
{
    /// <summary>Missing or invalid command arguments.</summary>
    public const string Validation = "VALIDATION";

    /// <summary>Referenced resource (file, task, room, agent, memory) does not exist.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>Agent lacks permission for the operation or path.</summary>
    public const string Permission = "PERMISSION";

    /// <summary>Operation conflicts with current state (e.g., task already claimed, room has participants).</summary>
    public const string Conflict = "CONFLICT";

    /// <summary>Operation exceeded its time limit.</summary>
    public const string Timeout = "TIMEOUT";

    /// <summary>Runtime execution failure (process crash, non-zero exit code).</summary>
    public const string Execution = "EXECUTION";

    /// <summary>Unexpected internal error.</summary>
    public const string Internal = "INTERNAL";

    /// <summary>
    /// Infer an error code from an <see cref="InvalidOperationException"/> message
    /// when the service layer doesn't throw typed exceptions. Best-effort heuristic.
    /// </summary>
    public static string Infer(string message)
    {
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return NotFound;
        if (message.Contains("already", StringComparison.OrdinalIgnoreCase)
            || message.Contains("must be", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot", StringComparison.OrdinalIgnoreCase)
            || message.Contains("is not", StringComparison.OrdinalIgnoreCase))
            return Conflict;
        return Execution;
    }
}

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
