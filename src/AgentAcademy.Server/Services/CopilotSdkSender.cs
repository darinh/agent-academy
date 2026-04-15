using System.Text;
using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.Contracts;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Sends prompts to a <see cref="CopilotSession"/>, collects streamed
/// responses, classifies SDK errors into typed exceptions, and retries
/// transient/quota failures with exponential backoff.
/// </summary>
public sealed class CopilotSdkSender
{
    private const int TransientMaxRetries = 3;
    private static readonly TimeSpan[] TransientBackoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    ];

    private const int QuotaMaxRetries = 3;
    private static readonly TimeSpan[] QuotaBackoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    private readonly ILogger<CopilotSdkSender> _logger;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IAgentErrorTracker _errorTracker;
    private readonly AgentQuotaService _quotaService;
    private readonly IActivityBroadcaster _activityBus;

    public CopilotSdkSender(
        ILogger<CopilotSdkSender> logger,
        ILlmUsageTracker usageTracker,
        IAgentErrorTracker errorTracker,
        AgentQuotaService quotaService,
        IActivityBroadcaster activityBus)
    {
        _logger = logger;
        _usageTracker = usageTracker;
        _errorTracker = errorTracker;
        _quotaService = quotaService;
        _activityBus = activityBus;
    }

    /// <summary>
    /// Sends a prompt and returns the complete response, retrying on
    /// transient and quota errors. Auth errors are never retried.
    /// </summary>
    public async Task<string> SendWithRetryAsync(
        CopilotSession session,
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            // Enforce quota before each attempt (including retries)
            await _quotaService.EnforceQuotaAsync(agent.Id);

            try
            {
                return await SendAsync(session, agent, prompt, roomId, ct);
            }
            catch (CopilotAuthException)
            {
                throw; // Never retry auth failures
            }
            catch (CopilotAuthorizationException)
            {
                throw; // Never retry authorization failures
            }
            catch (CopilotQuotaException ex)
            {
                if (attempt >= QuotaMaxRetries)
                {
                    _logger.LogWarning(
                        "Quota/rate-limit error for {AgentId} after {Attempts} retries — giving up",
                        agent.Id, attempt + 1);
                    await _errorTracker.RecordAsync(agent.Id, roomId, "quota",
                        ex.Message, recoverable: false, retried: true, retryAttempt: attempt + 1);
                    throw;
                }

                var delay = QuotaBackoff[Math.Min(attempt, QuotaBackoff.Length - 1)];
                _logger.LogWarning(
                    "Quota/rate-limit error for {AgentId} (attempt {Attempt}/{Max}), retrying in {Delay}s: {Error}",
                    agent.Id, attempt + 1, QuotaMaxRetries, delay.TotalSeconds, ex.Message);
                await _errorTracker.RecordAsync(agent.Id, roomId, "quota",
                    ex.Message, recoverable: true, retried: true, retryAttempt: attempt + 1);
                await Task.Delay(delay, ct);
            }
            catch (CopilotTransientException ex)
            {
                if (attempt >= TransientMaxRetries)
                {
                    _logger.LogWarning(
                        "Transient error for {AgentId} after {Attempts} retries — giving up",
                        agent.Id, attempt + 1);
                    await _errorTracker.RecordAsync(agent.Id, roomId, "transient",
                        ex.Message, recoverable: false, retried: true, retryAttempt: attempt + 1);
                    throw;
                }

                var delay = TransientBackoff[Math.Min(attempt, TransientBackoff.Length - 1)];
                _logger.LogWarning(
                    "Transient error for {AgentId} (attempt {Attempt}/{Max}), retrying in {Delay}s: {Error}",
                    agent.Id, attempt + 1, TransientMaxRetries, delay.TotalSeconds, ex.Message);
                await _errorTracker.RecordAsync(agent.Id, roomId, "transient",
                    ex.Message, recoverable: true, retried: true, retryAttempt: attempt + 1);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>
    /// Sends a prompt and collects the complete streamed response.
    /// Used for both normal sends and session priming.
    /// </summary>
    public async Task<string> CollectResponseAsync(
        CopilotSession session,
        string prompt,
        string agentId,
        string? roomId,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var done = new TaskCompletionSource();
        AssistantUsageEvent? capturedUsage = null;

        using var registration = ct.Register(() => done.TrySetCanceled(ct));

        var unsubscribe = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    sb.Append(delta.Data.DeltaContent);
                    break;
                case AssistantMessageEvent msg:
                    // Final complete message — overwrite streamed content
                    // if we got a final aggregated version.
                    if (!string.IsNullOrEmpty(msg.Data.Content))
                    {
                        sb.Clear();
                        sb.Append(msg.Data.Content);
                    }
                    break;
                case AssistantUsageEvent usage:
                    capturedUsage = usage;
                    break;
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
                case SessionErrorEvent err:
                    done.TrySetException(ClassifyError(err));
                    break;
            }
        });

        try
        {
            await session.SendAsync(new MessageOptions { Prompt = prompt });

            // Wait for the response — no internal timeout.
            // Cancellation is handled by the registration above.
            await done.Task;

            // Persist usage metrics (fire-and-forget style — errors are caught internally)
            if (capturedUsage is not null)
            {
                var data = capturedUsage.Data;
                await _usageTracker.RecordAsync(
                    agentId, roomId,
                    data.Model,
                    data.InputTokens, data.OutputTokens,
                    data.CacheReadTokens, data.CacheWriteTokens,
                    data.Cost, data.Duration,
                    data.ApiCallId, data.Initiator,
                    data.ReasoningEffort);

                // Broadcast context window usage update via SignalR
                var rawInput = data.InputTokens ?? 0;
                var inputTokens = double.IsFinite(rawInput) ? (long)Math.Clamp(rawInput, 0, long.MaxValue) : 0L;
                if (inputTokens > 0)
                {
                    var maxTokens = ModelContextLimits.GetLimit(data.Model);
                    var pct = maxTokens > 0
                        ? Math.Round((double)inputTokens / maxTokens * 100, 1)
                        : 0;
                    _activityBus.Broadcast(new ActivityEvent(
                        Id: Guid.NewGuid().ToString("N"),
                        Type: ActivityEventType.ContextUsageUpdated,
                        Severity: pct >= 80 ? ActivitySeverity.Warning : ActivitySeverity.Info,
                        RoomId: roomId,
                        ActorId: agentId,
                        TaskId: null,
                        Message: $"Context: {inputTokens:N0}/{maxTokens:N0} tokens ({pct}%)",
                        CorrelationId: null,
                        OccurredAt: DateTime.UtcNow,
                        Metadata: new Dictionary<string, object?>
                        {
                            ["currentTokens"] = inputTokens,
                            ["maxTokens"] = maxTokens,
                            ["percentage"] = pct,
                            ["model"] = data.Model,
                        }
                    ));
                }
            }

            return sb.ToString();
        }
        finally
        {
            unsubscribe.Dispose();
        }
    }

    /// <summary>
    /// Classifies a <see cref="SessionErrorEvent"/> into a typed exception
    /// based on the <c>ErrorType</c> field from the Copilot SDK.
    /// </summary>
    internal static Exception ClassifyError(SessionErrorEvent err)
    {
        var errorType = err.Data.ErrorType?.ToLowerInvariant();
        var message = err.Data.Message ?? "Unknown Copilot session error";

        return errorType switch
        {
            "authentication" => new CopilotAuthException(message),
            "authorization" => new CopilotAuthorizationException(message),
            "quota" => new CopilotQuotaException(errorType, message),
            "rate_limit" => new CopilotQuotaException(errorType, message),
            _ => new CopilotTransientException(message),
        };
    }

    private async Task<string> SendAsync(
        CopilotSession session,
        AgentDefinition agent,
        string prompt,
        string? roomId,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "Sending prompt to {AgentId}: {PromptPreview}...",
            agent.Id,
            prompt.Length > 80 ? prompt[..80] : prompt);

        var response = await CollectResponseAsync(session, prompt, agent.Id, roomId, ct);

        _logger.LogDebug(
            "Received response from {AgentId}: {Length} chars",
            agent.Id, response.Length);

        return response;
    }
}
