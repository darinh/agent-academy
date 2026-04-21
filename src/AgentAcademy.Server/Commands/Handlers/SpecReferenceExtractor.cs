using System.Text.RegularExpressions;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Extracts and validates file path references from spec markdown content.
/// Used by Tier 3 spec verification commands.
/// </summary>
internal static class SpecReferenceExtractor
{
    // Matches paths in backticks that look like source files
    // e.g., `src/AgentAcademy.Server/Services/SpecManager.cs`
    private static readonly Regex BacktickPathRegex = new(
        @"`((?:src|tests|specs|docs)/[^\s`]+\.\w+)`",
        RegexOptions.Compiled);

    // Matches **File**:, **Files**:, **Evidence**:, **Interface**:, **Entities**: patterns
    private static readonly Regex LabeledPathRegex = new(
        @"\*\*(?:File|Files|Evidence|Interface|Entities)\*\*:\s*(.+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Matches parenthetical paths like (src/AgentAcademy.Server/Services/Foo.cs)
    private static readonly Regex ParenPathRegex = new(
        @"\((`?)((?:src|tests|specs|docs)/[^\s`)]+\.\w+)\1\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts all file path references from spec markdown content.
    /// Returns distinct paths in order of first appearance.
    /// </summary>
    public static List<string> ExtractFilePaths(string specContent)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        void Add(string path)
        {
            // Strip backticks if present
            path = path.Trim('`', ' ');
            if (!string.IsNullOrWhiteSpace(path) && paths.Add(path))
                ordered.Add(path);
        }

        // Extract from labeled lines (**File**: ...)
        foreach (Match match in LabeledPathRegex.Matches(specContent))
        {
            var value = match.Groups[1].Value;
            // May contain multiple comma-separated paths
            foreach (var segment in value.Split(','))
            {
                var cleaned = segment.Trim().Trim('`', ' ');
                if (LooksLikeFilePath(cleaned))
                    Add(cleaned);
            }
        }

        // Extract backtick paths
        foreach (Match match in BacktickPathRegex.Matches(specContent))
        {
            Add(match.Groups[1].Value);
        }

        // Extract parenthetical paths
        foreach (Match match in ParenPathRegex.Matches(specContent))
        {
            Add(match.Groups[2].Value);
        }

        return ordered;
    }

    /// <summary>
    /// Validates extracted paths against the filesystem.
    /// Returns a list of (path, exists) tuples.
    /// </summary>
    public static List<PathValidation> ValidatePaths(
        IEnumerable<string> paths, string projectRoot)
    {
        var results = new List<PathValidation>();

        foreach (var path in paths)
        {
            var fullPath = Path.Combine(projectRoot, path);

            // Guard against path traversal
            var resolved = Path.GetFullPath(fullPath);
            var rootResolved = Path.GetFullPath(projectRoot);
            if (!resolved.StartsWith(rootResolved + Path.DirectorySeparatorChar) &&
                resolved != rootResolved)
            {
                results.Add(new PathValidation(path, false, "Path traversal blocked"));
                continue;
            }

            var exists = File.Exists(fullPath) || Directory.Exists(fullPath);
            results.Add(new PathValidation(path, exists, exists ? null : "Not found"));
        }

        return results;
    }

    /// <summary>
    /// Extracts handler class names referenced in the spec (e.g., RememberHandler.cs → REMEMBER).
    /// Useful for command inventory verification.
    /// </summary>
    public static List<string> ExtractHandlerNames(string specContent)
    {
        var regex = new Regex(@"`(\w+Handler)\.cs`", RegexOptions.Compiled);
        return regex.Matches(specContent)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool LooksLikeFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length < 5) return false;

        // Must start with a known root directory or contain a path separator
        return value.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("specs/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("docs/", StringComparison.OrdinalIgnoreCase);
    }

    internal record PathValidation(string Path, bool Exists, string? Reason);
}
