namespace AgentAcademy.Forge.Llm;

/// <summary>
/// Test stub for ILlmClient. Responses are configured via a delegate.
/// Default behavior: returns empty JSON object.
/// </summary>
public sealed class StubLlmClient : ILlmClient
{
    private readonly Func<LlmRequest, CancellationToken, Task<LlmResponse>> _handler;

    public StubLlmClient(Func<LlmRequest, CancellationToken, Task<LlmResponse>>? handler = null)
    {
        _handler = handler ?? DefaultHandler;
    }

    /// <summary>All requests received, in order.</summary>
    public List<LlmRequest> ReceivedRequests { get; } = [];

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        ReceivedRequests.Add(request);
        return await _handler(request, ct);
    }

    /// <summary>
    /// Create a stub that returns a fixed response for every request.
    /// </summary>
    public static StubLlmClient WithFixedResponse(string content, int inputTokens = 100, int outputTokens = 200)
    {
        return new StubLlmClient((req, _) => Task.FromResult(new LlmResponse
        {
            Content = content,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Model = req.Model,
            LatencyMs = 50
        }));
    }

    /// <summary>
    /// Create a stub that throws on every request.
    /// </summary>
    public static StubLlmClient WithError(LlmErrorKind errorKind, string message = "Stub error")
    {
        return new StubLlmClient((_, _) =>
            throw new LlmClientException(errorKind, message));
    }

    private static Task<LlmResponse> DefaultHandler(LlmRequest req, CancellationToken _)
    {
        return Task.FromResult(new LlmResponse
        {
            Content = "{}",
            InputTokens = 10,
            OutputTokens = 5,
            Model = req.Model,
            LatencyMs = 1
        });
    }
}
