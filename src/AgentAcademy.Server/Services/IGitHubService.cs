namespace AgentAcademy.Server.Services;

/// <summary>
/// Abstraction for GitHub API operations (PRs, status checks).
/// Enables testing without real GitHub connectivity.
/// </summary>
public interface IGitHubService
{
    /// <summary>
    /// Returns true when gh CLI is authenticated and can reach the GitHub API.
    /// </summary>
    Task<bool> IsConfiguredAsync();

    /// <summary>
    /// Returns the owner/repo slug derived from the git remote origin URL.
    /// Returns null if no remote is configured.
    /// </summary>
    Task<string?> GetRepositorySlugAsync();

    /// <summary>
    /// Creates a pull request on GitHub and returns its metadata.
    /// </summary>
    Task<PullRequestInfo> CreatePullRequestAsync(string branch, string title, string body, string baseBranch = "develop");

    /// <summary>
    /// Gets the current status of a pull request by number.
    /// </summary>
    Task<PullRequestInfo> GetPullRequestAsync(int prNumber);
}

/// <summary>
/// Lightweight DTO for PR metadata returned by the GitHub CLI.
/// </summary>
public record PullRequestInfo(
    int Number,
    string Url,
    string State,
    string Title,
    string BaseBranch,
    string HeadBranch,
    bool IsMerged,
    string? ReviewDecision = null
);
