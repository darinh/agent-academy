using System.Threading.RateLimiting;

namespace AgentAcademy.Server.Auth;

/// <summary>
/// Creates and configures rate limiting for consultant API requests.
/// Extracted for testability — the partitioner logic can be verified in isolation.
/// </summary>
public static class ConsultantRateLimitExtensions
{
    /// <summary>
    /// Creates a partitioned rate limiter that applies sliding window limits
    /// only to consultant-authenticated requests, with separate buckets for
    /// write operations (POST/PUT/DELETE/PATCH) and read operations.
    /// Non-consultant requests pass through with no limit.
    /// </summary>
    public static PartitionedRateLimiter<HttpContext> CreateConsultantRateLimiter(
        ConsultantRateLimitSettings settings)
    {
        return PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (context.User?.IsInRole("Consultant") != true)
                return RateLimitPartition.GetNoLimiter(string.Empty);

            var isWrite = HttpMethods.IsPost(context.Request.Method)
                || HttpMethods.IsPut(context.Request.Method)
                || HttpMethods.IsDelete(context.Request.Method)
                || HttpMethods.IsPatch(context.Request.Method);

            return isWrite
                ? RateLimitPartition.GetSlidingWindowLimiter("consultant-write", _ =>
                    new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = settings.WritePermitLimit,
                        Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                        SegmentsPerWindow = settings.SegmentsPerWindow,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    })
                : RateLimitPartition.GetSlidingWindowLimiter("consultant-read", _ =>
                    new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = settings.ReadPermitLimit,
                        Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                        SegmentsPerWindow = settings.SegmentsPerWindow,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    });
        });
    }
}
