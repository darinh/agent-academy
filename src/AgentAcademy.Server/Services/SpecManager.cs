using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages the living project specification that lives in the specs/ directory.
/// Provides methods to load spec context for prompt injection, list spec sections,
/// read individual spec content, and track spec versioning.
/// </summary>
public sealed class SpecManager
{
    /// <summary>
    /// Represents a single spec section with its metadata.
    /// </summary>
    public record SpecSection(string Id, string Heading, string Summary, string FilePath);

    /// <summary>
    /// Version information for the spec corpus, read from specs/spec-version.json.
    /// </summary>
    public record SpecVersionInfo(string Version, string LastUpdated, string ContentHash, int SectionCount);

    private readonly string _specsDir;
    private readonly ILogger<SpecManager>? _logger;

    // Cached content hash to avoid recomputing on every call
    private string? _cachedContentHash;
    private DateTime _cacheTimestamp;
    private int _cacheFileCount;

    /// <summary>
    /// Creates a SpecManager that reads from the given specs directory.
    /// Defaults to "specs" relative to the current working directory.
    /// </summary>
    public SpecManager(string? specsDir = null, ILogger<SpecManager>? logger = null)
    {
        _specsDir = specsDir ?? Path.Combine(Directory.GetCurrentDirectory(), "specs");
        _logger = logger;
    }

    /// <summary>
    /// Reads the specs/ directory and returns a condensed index (heading + purpose per
    /// section) suitable for prompt injection. Returns null if no specs exist.
    /// </summary>
    public async Task<string?> LoadSpecContextAsync()
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

                var content = await File.ReadAllTextAsync(specFile);
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
    public async Task<List<SpecSection>> GetSpecSectionsAsync()
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

                var content = await File.ReadAllTextAsync(specFile);
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
    public async Task<string?> GetSpecContentAsync(string sectionId)
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
            return await File.ReadAllTextAsync(specFile);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads spec context filtered to only the sections linked to a task.
    /// Falls back to full context if no links exist or if linkedSectionIds is empty.
    /// </summary>
    public async Task<string?> LoadSpecContextForTaskAsync(IEnumerable<string> linkedSectionIds)
    {
        var sectionIdSet = linkedSectionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sectionIdSet.Count == 0)
            return await LoadSpecContextAsync();

        if (!Directory.Exists(_specsDir)) return null;

        try
        {
            var sections = new List<string>();

            foreach (var dir in Directory.GetDirectories(_specsDir).OrderBy(d => d))
            {
                var dirName = Path.GetFileName(dir);
                var specFile = Path.Combine(dir, "spec.md");
                if (!File.Exists(specFile)) continue;

                var isLinked = sectionIdSet.Contains(dirName);

                var content = await File.ReadAllTextAsync(specFile);
                var headingMatch = Regex.Match(content, @"^#\s+(.+)", RegexOptions.Multiline);
                var heading = headingMatch.Success ? headingMatch.Groups[1].Value : dirName;

                var purposeMatch = Regex.Match(content, @"## Purpose\s*\n([\s\S]*?)(?=\n##|\z)");
                var summary = purposeMatch.Success
                    ? purposeMatch.Groups[1].Value.Trim().Split('\n')[0]
                    : "";

                var marker = isLinked ? "★" : " ";
                sections.Add($"- [{marker}] specs/{dirName}/spec.md: {heading}" +
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
    /// Returns version information for the spec corpus.
    /// Reads declared version from specs/spec-version.json and computes a content hash
    /// of all spec section files (specs/*/spec.md) for freshness detection.
    /// Returns null if no specs directory exists.
    /// </summary>
    public async Task<SpecVersionInfo?> GetSpecVersionAsync()
    {
        if (!Directory.Exists(_specsDir)) return null;

        var (version, lastUpdated) = await ReadVersionFileAsync();
        var contentHash = await ComputeContentHashAsync();
        var sectionCount = CountSpecSections();

        return new SpecVersionInfo(
            Version: version ?? "0.0.0",
            LastUpdated: lastUpdated ?? "unknown",
            ContentHash: contentHash,
            SectionCount: sectionCount);
    }

    /// <summary>
    /// Computes a SHA256 hash of all spec section files (specs/*/spec.md) for freshness detection.
    /// Uses sorted paths and normalized line endings for deterministic output.
    /// Result is cached in memory and invalidated when any spec file's write time changes.
    /// </summary>
    public async Task<string> ComputeContentHashAsync()
    {
        if (!Directory.Exists(_specsDir)) return "";

        // Check if cache is still valid (file count + newest write time unchanged)
        var (newestWrite, fileCount) = GetSpecFileMetadata();
        if (_cachedContentHash is not null && newestWrite == _cacheTimestamp && fileCount == _cacheFileCount)
            return _cachedContentHash;

        try
        {
            using var sha256 = SHA256.Create();
            using var stream = new MemoryStream();

            var specFiles = Directory.GetDirectories(_specsDir)
                .OrderBy(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "spec.md"))
                .Where(File.Exists);

            foreach (var file in specFiles)
            {
                var relativePath = Path.GetRelativePath(_specsDir, file);
                var pathBytes = Encoding.UTF8.GetBytes(relativePath + "\n");
                await stream.WriteAsync(pathBytes);

                var content = await File.ReadAllTextAsync(file);
                var normalized = content.Replace("\r\n", "\n");
                var contentBytes = Encoding.UTF8.GetBytes(normalized);
                await stream.WriteAsync(contentBytes);
            }

            stream.Position = 0;
            var hashBytes = await sha256.ComputeHashAsync(stream);
            var hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant()[..12];

            _cachedContentHash = hash;
            _cacheTimestamp = newestWrite;
            _cacheFileCount = fileCount;
            return hash;
        }
        catch
        {
            return "";
        }
    }

    private async Task<(string? Version, string? LastUpdated)> ReadVersionFileAsync()
    {
        var versionFile = Path.Combine(_specsDir, "spec-version.json");
        if (!File.Exists(versionFile)) return (null, null);

        try
        {
            var json = await File.ReadAllTextAsync(versionFile);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            var lastUpdated = root.TryGetProperty("lastUpdated", out var lu) ? lu.GetString() : null;

            return (version, lastUpdated);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Malformed spec-version.json — ignoring declared version");
            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private int CountSpecSections()
    {
        if (!Directory.Exists(_specsDir)) return 0;

        return Directory.GetDirectories(_specsDir)
            .Count(d => File.Exists(Path.Combine(d, "spec.md")));
    }

    private (DateTime NewestWrite, int FileCount) GetSpecFileMetadata()
    {
        if (!Directory.Exists(_specsDir)) return (DateTime.MinValue, 0);

        var newest = DateTime.MinValue;
        var count = 0;
        foreach (var dir in Directory.GetDirectories(_specsDir))
        {
            var specFile = Path.Combine(dir, "spec.md");
            if (!File.Exists(specFile)) continue;
            count++;
            var writeTime = File.GetLastWriteTimeUtc(specFile);
            if (writeTime > newest) newest = writeTime;
        }
        return (newest, count);
    }
}
