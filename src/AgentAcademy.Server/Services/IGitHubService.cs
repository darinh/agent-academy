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

    /// <summary>
    /// Posts a review on a pull request.
    /// </summary>
    /// <param name="prNumber">The PR number.</param>
    /// <param name="body">Review body text.</param>
    /// <param name="action">APPROVE, REQUEST_CHANGES, or COMMENT.</param>
    Task PostPrReviewAsync(int prNumber, string body, PrReviewAction action = PrReviewAction.Comment);

    /// <summary>
    /// Gets all reviews on a pull request.
    /// </summary>
    Task<IReadOnlyList<PullRequestReview>> GetPrReviewsAsync(int prNumber);

    /// <summary>
    /// Squash-merges a pull request on GitHub and returns the merge result.
    /// Optionally deletes the head branch after merging.
    /// </summary>
    Task<PrMergeResult> MergePullRequestAsync(int prNumber, string? commitTitle = null, bool deleteBranch = false);
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

/// <summary>
/// A single review on a pull request.
/// </summary>
public record PullRequestReview(
    string Author,
    string Body,
    string State,
    DateTime? SubmittedAt
);

/// <summary>
/// The action to take when posting a PR review.
/// </summary>
public enum PrReviewAction
{
    Comment,
    Approve,
    RequestChanges
}

/// <summary>
/// Result of merging a pull request via the GitHub API.
/// </summary>
public record PrMergeResult(
    int PrNumber,
    string? MergeCommitSha
);
