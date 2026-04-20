using AgentAcademy.Forge.Llm;

namespace AgentAcademy.Forge.Tests;

public sealed class LlmClientTests
{
    [Fact]
    public async Task StubLlmClient_DefaultHandler_ReturnsEmptyJson()
    {
        var client = new StubLlmClient();
        var request = new LlmRequest
        {
            SystemMessage = "test system",
            UserMessage = "test user",
            Model = "test-model"
        };

        var response = await client.GenerateAsync(request);

        Assert.Equal("{}", response.Content);
        Assert.Equal("test-model", response.Model);
        Assert.Single(client.ReceivedRequests);
        Assert.Same(request, client.ReceivedRequests[0]);
    }

    [Fact]
    public async Task StubLlmClient_WithFixedResponse_ReturnsConfiguredContent()
    {
        var client = StubLlmClient.WithFixedResponse("""{"body":{"x":1}}""", inputTokens: 50, outputTokens: 100);
        var request = new LlmRequest
        {
            SystemMessage = "sys",
            UserMessage = "usr",
            Model = "claude-sonnet-4.5"
        };

        var response = await client.GenerateAsync(request);

        Assert.Equal("""{"body":{"x":1}}""", response.Content);
        Assert.Equal(50, response.InputTokens);
        Assert.Equal(100, response.OutputTokens);
        Assert.Equal("claude-sonnet-4.5", response.Model);
    }

    [Fact]
    public async Task StubLlmClient_WithError_ThrowsLlmClientException()
    {
        var client = StubLlmClient.WithError(LlmErrorKind.Timeout, "Request timed out");

        var ex = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(new LlmRequest
            {
                SystemMessage = "sys",
                UserMessage = "usr",
                Model = "model"
            }));

        Assert.Equal(LlmErrorKind.Timeout, ex.ErrorKind);
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task StubLlmClient_CustomHandler_ReceivesRequestAndCancellation()
    {
        var receivedCt = CancellationToken.None;
        var client = new StubLlmClient((req, ct) =>
        {
            receivedCt = ct;
            return Task.FromResult(new LlmResponse
            {
                Content = $"echo: {req.UserMessage}",
                InputTokens = 1,
                OutputTokens = 2,
                Model = req.Model,
                LatencyMs = 0
            });
        });

        using var cts = new CancellationTokenSource();
        var response = await client.GenerateAsync(new LlmRequest
        {
            SystemMessage = "sys",
            UserMessage = "hello",
            Model = "m"
        }, cts.Token);

        Assert.Equal("echo: hello", response.Content);
        Assert.Equal(cts.Token, receivedCt);
    }

    [Fact]
    public void LlmRequest_DefaultValues_MatchFrozenSpec()
    {
        var request = new LlmRequest
        {
            SystemMessage = "s",
            UserMessage = "u",
            Model = "m"
        };

        Assert.Equal(0.2, request.Temperature);
        Assert.Equal(8192, request.MaxTokens);
        Assert.Equal(120, request.TimeoutSeconds);
        Assert.True(request.JsonMode);
    }

    [Fact]
    public void LlmClientException_CarriesErrorKind()
    {
        var inner = new TimeoutException("inner");
        var ex = new LlmClientException(LlmErrorKind.Transient, "rate limited", inner);

        Assert.Equal(LlmErrorKind.Transient, ex.ErrorKind);
        Assert.Equal("rate limited", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}
