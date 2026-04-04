using System.IO;
using System.Net.Http;
using Discord.Net;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Retry policy for transient notification provider failures.
/// Uses exponential backoff with jitter. Does not retry non-transient errors
/// (e.g., missing permissions, invalid configuration).
/// </summary>
public static class NotificationRetryPolicy
{
    public const int MaxRetries = 3;
    public const int BaseDelayMs = 200;
    public const int MaxDelayMs = 2000;
    public const int JitterMs = 50;

    /// <summary>
    /// Determines whether an exception represents a transient failure worth retrying.
    /// </summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException => true,
        HttpRequestException httpReq => IsTransientHttpRequestException(httpReq),
        IOException => true,
        HttpException httpEx => IsTransientHttpCode((int)httpEx.HttpCode),
        TaskCanceledException => false, // Caller-initiated cancellation — don't retry
        _ when ex.InnerException is not null => IsTransient(ex.InnerException),
        _ => false
    };

    /// <summary>
    /// Executes an operation with retry on transient failure.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (TaskCanceledException ex) when (attempt < MaxRetries)
            {
                // TaskCanceledException from HttpClient timeouts (not caller cancellation)
                var delay = CalculateDelay(attempt);
                logger.LogWarning(ex,
                    "Timeout on {Operation} (attempt {Attempt}/{Max}), retrying in {Delay}ms",
                    operationName, attempt + 1, MaxRetries, delay);
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                var delay = CalculateDelay(attempt);
                logger.LogWarning(ex,
                    "Transient failure on {Operation} (attempt {Attempt}/{Max}), retrying in {Delay}ms",
                    operationName, attempt + 1, MaxRetries, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Executes a void operation with retry on transient failure.
    /// </summary>
    public static async Task ExecuteAsync(
        Func<Task> operation,
        string operationName,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async () => { await operation(); return 0; },
            operationName, logger, cancellationToken);
    }

    /// <summary>
    /// Calculates the delay for a given retry attempt using exponential backoff with jitter.
    /// </summary>
    internal static int CalculateDelay(int attempt)
    {
        var baseDelay = Math.Min(BaseDelayMs * (1 << attempt), MaxDelayMs);
        var jitter = Random.Shared.Next(-JitterMs, JitterMs + 1);
        return Math.Max(0, baseDelay + jitter);
    }

    private static bool IsTransientHttpCode(int code) =>
        code == 429 || (code >= 500 && code <= 599);

    private static bool IsTransientHttpRequestException(HttpRequestException ex)
    {
        if (ex.StatusCode is null)
            return true; // Transport-level failure (no HTTP response) — transient
        return IsTransientHttpCode((int)ex.StatusCode.Value);
    }
}
