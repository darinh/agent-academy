namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Manages the living project specification in the specs/ directory.
/// </summary>
public interface ISpecManager
{
    /// <summary>
    /// Reads the specs/ directory and returns a condensed index suitable for prompt injection.
    /// Returns null if no specs exist.
    /// </summary>
    Task<string?> LoadSpecContextAsync();

    /// <summary>
    /// Lists all spec sections with metadata.
    /// Returns an empty list if no specs exist.
    /// </summary>
    Task<List<SpecSection>> GetSpecSectionsAsync();

    /// <summary>
    /// Reads the full content of a specific spec section by its directory name.
    /// Returns null if the section doesn't exist.
    /// </summary>
    Task<string?> GetSpecContentAsync(string sectionId);

    /// <summary>
    /// Loads spec context filtered to only the sections linked to a task.
    /// Falls back to full context if no links exist.
    /// </summary>
    Task<string?> LoadSpecContextForTaskAsync(IEnumerable<string> linkedSectionIds);

    /// <summary>
    /// Searches spec content by keywords and returns sections ranked by relevance.
    /// </summary>
    Task<List<SpecSearchResult>> SearchSpecsAsync(
        string query, int maxResults = 5, CancellationToken ct = default);

    /// <summary>
    /// Loads spec context with keyword-based relevance ranking.
    /// </summary>
    Task<string?> LoadSpecContextWithRelevanceAsync(
        string? searchQuery, IEnumerable<string>? linkedSectionIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns version information for the spec corpus.
    /// Returns null if no specs directory exists.
    /// </summary>
    Task<SpecVersionInfo?> GetSpecVersionAsync();

    /// <summary>
    /// Computes a SHA256 hash of all spec section files for freshness detection.
    /// </summary>
    Task<string> ComputeContentHashAsync();
}
