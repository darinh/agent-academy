namespace AgentAcademy.Forge.Llm;

/// <summary>
/// Infrastructure-level LLM failure. Classified by <see cref="ErrorKind"/>
/// so the PhaseExecutor can decide whether to record Errored vs. retry at
/// the HTTP level.
/// </summary>
public sealed class LlmClientException : Exception
{
    public LlmErrorKind ErrorKind { get; }

    public LlmClientException(LlmErrorKind errorKind, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorKind = errorKind;
    }
}

/// <summary>
/// Classification of LLM infrastructure failures.
/// </summary>
public enum LlmErrorKind
{
    /// <summary>HTTP 429 or 503 — safe to retry after backoff.</summary>
    Transient,

    /// <summary>Request timed out (no response within deadline).</summary>
    Timeout,

    /// <summary>HTTP 401/403 — auth failure, do not retry.</summary>
    Authentication,

    /// <summary>HTTP 400 — bad request (prompt too long, invalid model). Do not retry with same input.</summary>
    BadRequest,

    /// <summary>Provider returned a response but it's empty or unparseable at the transport level.</summary>
    MalformedResponse,

    /// <summary>Unknown/unclassified failure.</summary>
    Unknown
}
