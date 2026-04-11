namespace AgentAcademy.Server.Services;

/// <summary>
/// Base class for Copilot SDK errors that carry the original error type
/// classification from <c>SessionErrorEvent.Data.ErrorType</c>.
/// </summary>
public abstract class CopilotException : Exception
{
    public string ErrorType { get; }

    protected CopilotException(string errorType, string message)
        : base(message)
    {
        ErrorType = errorType;
    }
}

/// <summary>
/// Thrown when the Copilot SDK reports an authentication failure.
/// This is a definitive error — the current token is invalid and
/// retrying with the same token will not help.
/// </summary>
public sealed class CopilotAuthException : CopilotException
{
    public CopilotAuthException(string message)
        : base("authentication", message) { }
}

/// <summary>
/// Thrown when the Copilot SDK reports an authorization failure.
/// The token is valid but lacks permissions for the requested operation.
/// </summary>
public sealed class CopilotAuthorizationException : CopilotException
{
    public CopilotAuthorizationException(string message)
        : base("authorization", message) { }
}

/// <summary>
/// Thrown when the Copilot SDK reports a transient error (network,
/// server-side 5xx, timeout). These are retryable.
/// </summary>
public sealed class CopilotTransientException : CopilotException
{
    public CopilotTransientException(string message)
        : base("transient", message) { }
}

/// <summary>
/// Thrown when the Copilot SDK reports a quota or rate limit error.
/// Retryable with longer backoff.
/// </summary>
public sealed class CopilotQuotaException : CopilotException
{
    public CopilotQuotaException(string errorType, string message)
        : base(errorType, message) { }
}

/// <summary>
/// Thrown when an agent exceeds its configured resource quota
/// (requests/hour, tokens/hour, or cost/hour). Not retryable
/// until the quota window resets.
/// </summary>
public sealed class AgentQuotaExceededException : Exception
{
    public string AgentId { get; }
    public string QuotaType { get; }
    public int RetryAfterSeconds { get; }

    public AgentQuotaExceededException(
        string agentId, string quotaType, string message, int retryAfterSeconds)
        : base(message)
    {
        AgentId = agentId;
        QuotaType = quotaType;
        RetryAfterSeconds = retryAfterSeconds;
    }
}
