using System.Text.RegularExpressions;

namespace AgentAcademy.Forge;

/// <summary>
/// Validation and normalization helpers for externally-supplied Forge identifiers.
/// </summary>
public static partial class ForgeIdentifiers
{
    [GeneratedRegex("^R_[0-9A-HJKMNP-TV-Z]{26}$")]
    private static partial Regex RunIdPattern();

    [GeneratedRegex("^(sha256:)?[0-9a-f]{64}$")]
    private static partial Regex ArtifactHashPattern();

    /// <summary>
    /// Validate run ID format: R_ + 26 Crockford Base32 chars.
    /// </summary>
    public static bool IsValidRunId(string? runId) =>
        !string.IsNullOrEmpty(runId) && RunIdPattern().IsMatch(runId);

    /// <summary>
    /// Validate and normalize an artifact hash. Strips optional sha256: prefix.
    /// Returns the raw 64-char hex hash, or null if invalid.
    /// </summary>
    public static string? NormalizeArtifactHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || !ArtifactHashPattern().IsMatch(hash))
            return null;

        return hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash[7..] : hash;
    }
}
