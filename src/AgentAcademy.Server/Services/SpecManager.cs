using System.Text.RegularExpressions;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages the living project specification that lives in the specs/ directory.
/// Provides methods to load spec context for prompt injection, list spec sections,
/// and read individual spec content.
/// </summary>
public sealed class SpecManager
{
    /// <summary>
    /// Represents a single spec section with its metadata.
    /// </summary>
    public record SpecSection(string Id, string Heading, string Summary, string FilePath);

    private readonly string _specsDir;

    /// <summary>
    /// Creates a SpecManager that reads from the given specs directory.
    /// Defaults to "specs" relative to the current working directory.
    /// </summary>
    public SpecManager(string? specsDir = null)
    {
        _specsDir = specsDir ?? Path.Combine(Directory.GetCurrentDirectory(), "specs");
    }

    /// <summary>
    /// Reads the specs/ directory and returns a condensed index (heading + purpose per
    /// section) suitable for prompt injection. Returns null if no specs exist.
    /// </summary>
    public string? LoadSpecContext()
    {
        if (!Directory.Exists(_specsDir)) return null;

        try
        {
            var sections = new List<string>();

            foreach (var dir in Directory.GetDirectories(_specsDir).OrderBy(d => d))
            {
                var dirName = Path.GetFileName(dir);
                var specFile = Path.Combine(dir, "spec.md");
                if (!File.Exists(specFile)) continue;

                var content = File.ReadAllText(specFile);
                var headingMatch = Regex.Match(content, @"^#\s+(.+)", RegexOptions.Multiline);
                var heading = headingMatch.Success ? headingMatch.Groups[1].Value : dirName;

                var purposeMatch = Regex.Match(content, @"## Purpose\s*\n([\s\S]*?)(?=\n##|\z)");
                var summary = purposeMatch.Success
                    ? purposeMatch.Groups[1].Value.Trim().Split('\n')[0]
                    : "";

                sections.Add($"- specs/{dirName}/spec.md: {heading}" +
                    (string.IsNullOrEmpty(summary) ? "" : $" — {summary}"));
            }

            return sections.Count == 0 ? null : string.Join("\n", sections);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lists all spec sections with metadata (id, heading, summary, file path).
    /// Returns an empty list if no specs exist.
    /// </summary>
    public List<SpecSection> GetSpecSections()
    {
        var result = new List<SpecSection>();

        if (!Directory.Exists(_specsDir)) return result;

        try
        {
            foreach (var dir in Directory.GetDirectories(_specsDir).OrderBy(d => d))
            {
                var dirName = Path.GetFileName(dir);
                var specFile = Path.Combine(dir, "spec.md");
                if (!File.Exists(specFile)) continue;

                var content = File.ReadAllText(specFile);
                var headingMatch = Regex.Match(content, @"^#\s+(.+)", RegexOptions.Multiline);
                var heading = headingMatch.Success ? headingMatch.Groups[1].Value : dirName;

                var purposeMatch = Regex.Match(content, @"## Purpose\s*\n([\s\S]*?)(?=\n##|\z)");
                var summary = purposeMatch.Success
                    ? purposeMatch.Groups[1].Value.Trim().Split('\n')[0]
                    : "";

                result.Add(new SpecSection(
                    Id: dirName,
                    Heading: heading,
                    Summary: summary,
                    FilePath: $"specs/{dirName}/spec.md"));
            }
        }
        catch
        {
            // Silently return empty on filesystem errors
        }

        return result;
    }

    /// <summary>
    /// Reads the full content of a specific spec section by its directory name
    /// (e.g., "001-domain-model"). Returns null if the section doesn't exist.
    /// </summary>
    public string? GetSpecContent(string sectionId)
    {
        if (string.IsNullOrWhiteSpace(sectionId)) return null;

        var specFile = Path.Combine(_specsDir, sectionId, "spec.md");

        // Guard against path traversal
        var fullPath = Path.GetFullPath(specFile);
        var fullSpecsDir = Path.GetFullPath(_specsDir);
        if (!fullPath.StartsWith(fullSpecsDir + Path.DirectorySeparatorChar))
            return null;

        if (!File.Exists(specFile)) return null;

        try
        {
            return File.ReadAllText(specFile);
        }
        catch
        {
            return null;
        }
    }
}
