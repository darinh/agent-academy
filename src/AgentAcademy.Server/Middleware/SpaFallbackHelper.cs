namespace AgentAcademy.Server.Middleware;

/// <summary>
/// Determines whether a request path should be handled by the SPA fallback
/// (i.e., serve index.html) or left to the server pipeline (404).
/// </summary>
public static class SpaFallbackHelper
{
    // Exact-root + subpath exclusions (must match /api, /api/..., /hubs, /hubs/...)
    private static readonly string[] ExactOrSubpathPrefixes = ["/api", "/hubs"];

    // Prefix exclusions (match anything starting with these — /health, /healthz, /swagger/...)
    private static readonly string[] PrefixExclusions = ["/health", "/swagger"];

    /// <summary>
    /// Returns true when the request path should receive the SPA index.html.
    /// Returns false for server-owned prefixes (/api, /hubs, /health, /swagger)
    /// whether accessed as exact roots or with subpaths.
    /// </summary>
    public static bool ShouldServeIndex(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        foreach (var prefix in ExactOrSubpathPrefixes)
        {
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var prefix in PrefixExclusions)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
