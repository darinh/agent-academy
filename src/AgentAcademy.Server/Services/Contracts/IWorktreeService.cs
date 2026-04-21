namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Manages git worktrees for task-level isolation. Each task gets its own
/// worktree directory so agents can work on different branches in parallel
/// without stash/checkout cycling on the main working tree.
/// </summary>
public interface IWorktreeService
{
    // ── Branch-Scoped Worktrees ─────────────────────────────────

    /// <summary>
    /// Creates a worktree for an existing branch, returning the worktree info.
    /// The branch must already exist. If a worktree already exists for this
    /// branch, returns the existing entry.
    /// </summary>
    Task<WorktreeInfo> CreateWorktreeAsync(string branch);

    /// <summary>
    /// Removes the worktree for a branch and cleans up the directory.
    /// Safe to call if no worktree exists for the branch.
    /// </summary>
    Task RemoveWorktreeAsync(string branch);

    /// <summary>
    /// Returns the filesystem path of the worktree for a branch, or null if none exists.
    /// </summary>
    string? GetWorktreePath(string branch);

    /// <summary>
    /// Returns all worktrees currently tracked in memory.
    /// </summary>
    IReadOnlyList<WorktreeInfo> GetActiveWorktrees();

    /// <summary>
    /// Lists all git worktrees by parsing <c>git worktree list --porcelain</c>.
    /// </summary>
    Task<IReadOnlyList<GitWorktreeEntry>> ListGitWorktreesAsync();

    /// <summary>
    /// Removes all tracked worktrees and cleans up the worktree root directory.
    /// </summary>
    Task CleanupAllWorktreesAsync();

    /// <summary>
    /// Re-scans the git worktree list and reconciles with in-memory state,
    /// removing entries whose directories no longer exist.
    /// </summary>
    Task SyncWithGitAsync();

    // ── Agent Worktrees ─────────────────────────────────────────

    /// <summary>
    /// Ensures an agent has a worktree checked out on the given branch.
    /// Creates the worktree if it doesn't exist. Returns the worktree path.
    /// </summary>
    Task<string> EnsureAgentWorktreeAsync(string workspacePath, string projectName, string agentId, string branch);

    /// <summary>
    /// Returns the filesystem path for an agent's worktree, or null if none exists.
    /// </summary>
    string? GetAgentWorktreePath(string projectName, string agentId, string? workspacePath = null);

    /// <summary>
    /// Removes an agent's worktree and cleans up the directory.
    /// </summary>
    Task RemoveAgentWorktreeAsync(string workspacePath, string projectName, string agentId);

    // ── Status & Queries ────────────────────────────────────────

    /// <summary>
    /// Returns the git status (dirty files, diff stats, last commit) for a worktree directory.
    /// </summary>
    Task<WorktreeGitStatus> GetWorktreeGitStatusAsync(string worktreePath, CancellationToken cancellationToken = default);

    /// <summary>Repository root path used to derive relative worktree paths.</summary>
    string RepositoryRoot { get; }
}
