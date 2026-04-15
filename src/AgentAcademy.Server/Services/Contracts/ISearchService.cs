using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for cross-entity search across messages, tasks, and agents.
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Searches messages, tasks, and agents matching the given query.
    /// </summary>
    Task<SearchResults> SearchAsync(
        string query,
        string scope = "all",
        int messageLimit = 20,
        int taskLimit = 20,
        string? workspacePath = null,
        CancellationToken ct = default);
}
