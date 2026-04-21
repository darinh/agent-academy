using System.Diagnostics;
using System.Text;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Server.Services.Contracts;
using GitHub.Copilot.SDK;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Forge ILlmClient implementation backed by the same Copilot SDK client/token
/// path used by agent execution.
/// </summary>
public sealed class CopilotSdkLlmClient : ILlmClient
{
    private readonly ICopilotClientFactory _clientFactory;
    private readonly ILogger<CopilotSdkLlmClient> _logger;

    public CopilotSdkLlmClient(
        ICopilotClientFactory clientFactory,
        ILogger<CopilotSdkLlmClient> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var token = timeoutCts.Token;

        var acquisition = await _clientFactory.GetClientAsync(token);
        if (acquisition.Client is null)
        {
            throw new LlmClientException(
                LlmErrorKind.Authentication,
                "Forge execution unavailable: Copilot SDK client could not be started. " +
                "Authenticate in Agent Academy so the SDK token is available.");
        }

        await using var session = await acquisition.Client.CreateSessionAsync(new SessionConfig
        {
            Model = request.Model,
            Streaming = true
        });

        var responseText = new StringBuilder();
        AssistantUsageEvent? usage = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelRegistration = token.Register(() => completed.TrySetCanceled(token));
        using var unsubscribe = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    responseText.Append(delta.Data.DeltaContent);
                    break;
                case AssistantMessageEvent msg:
                    if (!string.IsNullOrEmpty(msg.Data.Content))
                    {
                        responseText.Clear();
                        responseText.Append(msg.Data.Content);
                    }
                    break;
                case AssistantUsageEvent usageEvt:
                    usage = usageEvt;
                    break;
                case SessionIdleEvent:
                    completed.TrySetResult();
                    break;
                case SessionErrorEvent err:
                    completed.TrySetException(MapError(err));
                    break;
            }
        });

        var sw = Stopwatch.StartNew();
        try
        {
            await session.SendAsync(new MessageOptions
            {
                Prompt = BuildPrompt(request)
            });

            await completed.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmClientException(
                LlmErrorKind.Timeout,
                $"Copilot SDK request timed out after {request.TimeoutSeconds}s.");
        }
        finally
        {
            sw.Stop();
        }

        if (responseText.Length == 0)
        {
            _logger.LogWarning("Copilot SDK returned an empty response for model {Model}", request.Model);
        }

        return new LlmResponse
        {
            Content = responseText.ToString(),
            InputTokens = ToIntTokenCount(usage?.Data.InputTokens),
            OutputTokens = ToIntTokenCount(usage?.Data.OutputTokens),
            Model = usage?.Data.Model ?? request.Model,
            LatencyMs = sw.ElapsedMilliseconds
        };
    }

    private static string BuildPrompt(LlmRequest request)
    {
        var prompt = new StringBuilder()
            .AppendLine("System instructions:")
            .AppendLine(request.SystemMessage.Trim())
            .AppendLine()
            .AppendLine("User request:")
            .AppendLine(request.UserMessage.Trim());

        if (request.JsonMode)
        {
            prompt.AppendLine()
                .AppendLine("Return only a valid JSON object. Do not include markdown fences.");
        }

        return prompt.ToString();
    }

    private static int ToIntTokenCount(double? tokenCount)
    {
        if (tokenCount is null || double.IsNaN(tokenCount.Value) || double.IsInfinity(tokenCount.Value))
            return 0;

        var rounded = Math.Round(tokenCount.Value, MidpointRounding.AwayFromZero);
        return rounded switch
        {
            < 0 => 0,
            > int.MaxValue => int.MaxValue,
            _ => (int)rounded
        };
    }

    private static Exception MapError(SessionErrorEvent err)
    {
        var message = err.Data.Message ?? "Unknown Copilot SDK session error";
        var errorType = err.Data.ErrorType?.ToLowerInvariant();

        var kind = errorType switch
        {
            "authentication" => LlmErrorKind.Authentication,
            "authorization" => LlmErrorKind.Authentication,
            "quota" => LlmErrorKind.Transient,
            "rate_limit" => LlmErrorKind.Transient,
            "bad_request" => LlmErrorKind.BadRequest,
            _ => LlmErrorKind.Unknown
        };

        return new LlmClientException(kind, message);
    }
}
