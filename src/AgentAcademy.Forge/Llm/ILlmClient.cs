namespace AgentAcademy.Forge.Llm;

/// <summary>
/// Abstraction for calling an LLM. Deliberately thin — no retry logic,
/// no streaming, no tool use. The PhaseExecutor handles retries at the
/// attempt level.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Send a single-turn request and get a single response.
    /// Throws <see cref="LlmClientException"/> on infrastructure failures.
    /// </summary>
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default);
}

/// <summary>
/// Single-turn LLM request. Maps to the frozen call parameters from prompt-envelope.md.
/// </summary>
public sealed record LlmRequest
{
    public required string SystemMessage { get; init; }
    public required string UserMessage { get; init; }
    public required string Model { get; init; }
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 8192;
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// When true, request JSON object response format from the provider.
    /// Maps to the frozen response_format parameter.
    /// </summary>
    public bool JsonMode { get; init; } = true;
}

/// <summary>
/// LLM response with token counts and timing.
/// </summary>
public sealed record LlmResponse
{
    /// <summary>Raw response content from the LLM.</summary>
    public required string Content { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required string Model { get; init; }
    public required long LatencyMs { get; init; }
}
