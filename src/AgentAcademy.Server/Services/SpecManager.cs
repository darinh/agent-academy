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
/// <summary>
/// Represents a single spec section with its metadata.
/// </summary>
public record SpecSection(string Id, string Heading, string Summary, string FilePath);

/// <summary>
/// A spec section matched by keyword search, with a relevance score.
/// </summary>
public record SpecSearchResult(string Id, string Heading, string Summary, string FilePath, double Score, string MatchedTerms);

/// <summary>
/// Version information for the spec corpus, read from specs/spec-version.json.
/// </summary>
public record SpecVersionInfo(string Version, string LastUpdated, string ContentHash, int SectionCount);

public sealed class SpecManager : Contracts.ISpecManager
{
    private readonly string _specsDir;
    private readonly ILogger<SpecManager>? _logger;

    // Cached content hash to avoid recomputing on every call.
    // Invalidation key is a signature covering every spec file's (path, mtime, length) —
    // editing any file (even an older one) changes the signature.
    private string? _cachedContentHash;
    private string? _cacheSignature;

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
    /// Searches spec content by keywords and returns sections ranked by relevance.
    /// Splits the query into terms and scores each section by weighted term frequency
    /// (heading matches weighted 3×, purpose 2×, body 1×).
    /// </summary>
    public async Task<List<SpecSearchResult>> SearchSpecsAsync(
        string query, int maxResults = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !Directory.Exists(_specsDir))
            return [];

        var terms = TokenizeQuery(query);
        if (terms.Count == 0) return [];

        var scored = new List<SpecSearchResult>();

        try
        {
            foreach (var dir in Directory.GetDirectories(_specsDir).OrderBy(d => d))
            {
                ct.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(dir);
                var specFile = Path.Combine(dir, "spec.md");
                if (!File.Exists(specFile)) continue;

                var content = await File.ReadAllTextAsync(specFile, ct);
                var result = ScoreSection(dirName, content, terms);
                if (result is not null) scored.Add(result);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Spec search failed for query: {Query}", query);
            return [];
        }

        return scored
            .OrderByDescending(r => r.Score)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Loads spec context with keyword-based relevance ranking in a single pass.
    /// Relevant sections (matched by query or linked to task) are marked with ★/◆ and listed first.
    /// Non-matching sections are still included but listed after relevant ones.
    /// </summary>
    public async Task<string?> LoadSpecContextWithRelevanceAsync(
        string? searchQuery, IEnumerable<string>? linkedSectionIds = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(_specsDir)) return null;

        var linkedSet = linkedSectionIds?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var terms = !string.IsNullOrWhiteSpace(searchQuery)
            ? TokenizeQuery(searchQuery)
            : [];

        try
        {
            var relevant = new List<(string Line, double Score)>();
            var other = new List<string>();

            foreach (var dir in Directory.GetDirectories(_specsDir).OrderBy(d => d))
            {
                ct.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(dir);
                var specFile = Path.Combine(dir, "spec.md");
                if (!File.Exists(specFile)) continue;

                var content = await File.ReadAllTextAsync(specFile, ct);
                var headingMatch = Regex.Match(content, @"^#\s+(.+)", RegexOptions.Multiline);
                var heading = headingMatch.Success ? headingMatch.Groups[1].Value : dirName;

                var purposeMatch = Regex.Match(content, @"## Purpose\s*\n([\s\S]*?)(?=\n##|\z)");
                var summary = purposeMatch.Success
                    ? purposeMatch.Groups[1].Value.Trim().Split('\n')[0]
                    : "";

                var isLinked = linkedSet.Contains(dirName);
                var searchResult = terms.Count > 0 ? ScoreSection(dirName, content, terms) : null;
                var isSearchMatch = searchResult is not null;
                var isRelevant = isLinked || isSearchMatch;

                var marker = isLinked ? "★" : isSearchMatch ? "◆" : " ";
                var line = $"- [{marker}] specs/{dirName}/spec.md: {heading}" +
                    (string.IsNullOrEmpty(summary) ? "" : $" — {summary}");

                if (isRelevant)
                    relevant.Add((line, searchResult?.Score ?? 0));
                else
                    other.Add(line);
            }

            if (relevant.Count == 0 && other.Count == 0) return null;

            var parts = new List<string>();
            if (relevant.Count > 0)
            {
                parts.Add("Relevant sections (★ = task-linked, ◆ = keyword match):");
                // Sort relevant by score descending (linked without search score come first)
                parts.AddRange(relevant.OrderByDescending(r => r.Score).Select(r => r.Line));
            }
            if (other.Count > 0)
            {
                if (relevant.Count > 0)
                    parts.Add("\nOther sections:");
                parts.AddRange(other);
            }

            return string.Join("\n", parts);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    private static SpecSearchResult? ScoreSection(string dirName, string content, List<string> terms)
    {
        var headingMatch = Regex.Match(content, @"^#\s+(.+)", RegexOptions.Multiline);
        var heading = headingMatch.Success ? headingMatch.Groups[1].Value : dirName;

        var purposeMatch = Regex.Match(content, @"## Purpose\s*\n([\s\S]*?)(?=\n##|\z)");
        var purpose = purposeMatch.Success ? purposeMatch.Groups[1].Value.Trim() : "";

        var score = 0.0;
        var matched = new List<string>();

        foreach (var term in terms)
        {
            var headingHits = CountOccurrences(heading, term);
            var purposeHits = CountOccurrences(purpose, term);
            var bodyHits = CountOccurrences(content, term);
            var pureBodyHits = Math.Max(0, bodyHits - headingHits - purposeHits);

            var termScore = headingHits * 3.0 + purposeHits * 2.0 + pureBodyHits * 1.0;
            if (termScore > 0)
            {
                score += termScore;
                matched.Add(term);
            }
        }

        if (score <= 0) return null;

        var coverageBonus = (double)matched.Count / terms.Count;
        score *= (1.0 + coverageBonus);

        var summary = purpose.Split('\n')[0];
        return new SpecSearchResult(
            Id: dirName,
            Heading: heading,
            Summary: summary,
            FilePath: $"specs/{dirName}/spec.md",
            Score: Math.Round(score, 2),
            MatchedTerms: string.Join(", ", matched));
    }

    internal static List<string> TokenizeQuery(string query)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "shall", "can", "need", "dare", "ought",
            "to", "of", "in", "for", "on", "with", "at", "by", "from", "as",
            "into", "through", "during", "before", "after", "above", "below",
            "between", "out", "off", "over", "under", "again", "further", "then",
            "once", "and", "but", "or", "nor", "not", "so", "yet", "both",
            "each", "few", "more", "most", "other", "some", "such", "no", "only",
            "same", "than", "too", "very", "just", "because", "this", "that",
            "these", "those", "it", "its", "what", "which", "who", "whom", "how"
        };

        return Regex.Split(query.ToLowerInvariant(), @"[\s\-_./,;:!?()""]+")
            .Where(t => t.Length >= 3 && !stopWords.Contains(t))
            .Distinct()
            .ToList();
    }

    internal static int CountOccurrences(string text, string term)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return 0;

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }
        return count;
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

        // Check if cache is still valid — signature covers every spec file's mtime + size.
        var signature = ComputeCacheSignature();
        if (_cachedContentHash is not null && signature == _cacheSignature)
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
            _cacheSignature = signature;
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

    /// <summary>
    /// Builds a stable signature over every spec file's (path, mtime_ticks, length).
    /// Editing any file — including older ones — changes the signature and invalidates the cache.
    /// </summary>
    private string ComputeCacheSignature()
    {
        if (!Directory.Exists(_specsDir)) return "empty";

        var entries = new List<string>();
        foreach (var dir in Directory.GetDirectories(_specsDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var specFile = Path.Combine(dir, "spec.md");
            if (!File.Exists(specFile)) continue;
            var info = new FileInfo(specFile);
            entries.Add($"{specFile}|{info.LastWriteTimeUtc.Ticks}|{info.Length}");
        }
        return string.Join(";", entries);
    }
}
