namespace AgentAcademy.Server.Auth;

/// <summary>
/// Configuration for consultant API rate limiting.
/// Bound from <c>ConsultantApi:RateLimiting</c> in appsettings.json.
/// </summary>
public sealed record ConsultantRateLimitSettings
{
    public const string SectionName = "ConsultantApi:RateLimiting";

    /// <summary>Whether rate limiting is enabled. Defaults to true when consultant auth is configured.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Max write requests (POST/PUT/DELETE/PATCH) per window. Default 20.</summary>
    public int WritePermitLimit { get; init; } = 20;

    /// <summary>Max read requests (GET/HEAD/OPTIONS) per window. Default 60.</summary>
    public int ReadPermitLimit { get; init; } = 60;

    /// <summary>Sliding window duration in seconds. Default 60.</summary>
    public int WindowSeconds { get; init; } = 60;

    /// <summary>Number of segments in the sliding window. More segments = smoother rate limiting. Default 6.</summary>
    public int SegmentsPerWindow { get; init; } = 6;
}
