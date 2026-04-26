using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Git branch management, commit, merge, and rebase operations.
/// All operations are serialized to prevent concurrent branch switches
/// from corrupting the working tree.
/// </summary>
public interface IGitService
{
    // ── Round-Scoped Locking ────────────────────────────────────

    /// <summary>
    /// Acquires the round lock. Held during the entire agent execution round
    /// so branch switches are safe from concurrent orchestrator activity.
    /// </summary>
    Task AcquireRoundLockAsync();

    /// <summary>
    /// Releases the round lock after the agent execution round completes.
    /// </summary>
    void ReleaseRoundLock();

    // ── Branch Operations ───────────────────────────────────────

    /// <summary>
    /// Creates a task branch from develop with a unique suffix to avoid collisions.
    /// Returns the branch name (e.g. "task/my-feature-a1b2c3").
    /// </summary>
    Task<string> CreateTaskBranchAsync(string slug);

    /// <summary>
    /// Switches to the specified branch. Stashes any uncommitted changes
    /// (including untracked files) with a colon-delimited name for later recovery.
    /// Caller must hold the round lock.
    /// </summary>
    Task EnsureBranchInternalAsync(string branch);

    /// <summary>
    /// Stashes current work and returns to develop.
    /// Caller must hold the round lock.
    /// </summary>
    Task ReturnToDevelopInternalAsync(string branch);

    /// <summary>
    /// Self-locking convenience wrapper for returning to develop.
    /// </summary>
    Task ReturnToDevelopAsync(string branch);

    /// <summary>
    /// Checks out an existing branch.
    /// </summary>
    Task CheckoutBranchAsync(string branch);

    /// <summary>
    /// Checks whether a local branch exists.
    /// </summary>
    Task<bool> BranchExistsAsync(string branch);

    /// <summary>
    /// Deletes a branch. Refuses to delete develop or main.
    /// </summary>
    Task DeleteBranchAsync(string branch);

    // ── Commit Operations ───────────────────────────────────────

    /// <summary>
    /// Creates a commit from staged changes and returns the new commit SHA.
    /// When <paramref name="author"/> is provided, the commit uses --author
    /// to attribute the work to the specified agent identity.
    /// </summary>
    Task<string> CommitAsync(string message, AgentGitIdentity? author = null);

    /// <summary>
    /// Stages all changes and creates a commit in a specific directory.
    /// Returns the new commit SHA.
    /// </summary>
    Task<string> CommitInDirAsync(string workingDir, string message, AgentGitIdentity? author = null);

    /// <summary>
    /// Commits already-staged changes in <paramref name="workingDir"/> without
    /// running <c>git add -A</c> first. Used by per-worktree agent tool wrappers
    /// where the wrapper has already validated and staged its own paths and
    /// must not pull in unrelated untracked files. Returns the new commit SHA.
    /// </summary>
    Task<string> CommitStagedInDirAsync(string workingDir, string message, AgentGitIdentity? author = null);

    // ── Stash Operations ────────────────────────────────────────

    /// <summary>
    /// Pops the most recent auto-stash for the given branch.
    /// Returns true when a matching stash is restored.
    /// </summary>
    Task<bool> PopAutoStashAsync(string branch);

    // ── Push Operations ─────────────────────────────────────────

    /// <summary>
    /// Pushes a branch to the remote origin. Used before creating a PR.
    /// </summary>
    Task PushBranchAsync(string branch);

    // ── Info Queries ────────────────────────────────────────────

    /// <summary>
    /// Returns the current branch name in a specific directory.
    /// </summary>
    Task<string?> GetCurrentBranchInDirAsync(string workingDir);

    /// <summary>
    /// Returns the list of files changed in a commit (relative paths).
    /// </summary>
    Task<IReadOnlyList<string>> GetFilesInCommitAsync(string commitSha, string? workingDir = null);

    // ── Merge & Revert ──────────────────────────────────────────

    /// <summary>
    /// Squash-merges a task branch into the current branch (develop).
    /// Uses git reset --hard HEAD on failure to cleanly undo squash state.
    /// </summary>
    Task<string> SquashMergeAsync(string branch, string commitMessage, AgentGitIdentity? author = null);

    /// <summary>
    /// Reverts a commit on develop. Returns the SHA of the revert commit.
    /// </summary>
    Task<string> RevertCommitAsync(string commitSha);

    // ── Rebase & Conflict Detection ─────────────────────────────

    /// <summary>
    /// Performs a dry-run merge to detect conflicts between develop and a feature branch
    /// without modifying the working tree.
    /// </summary>
    Task<MergeConflictResult> DetectMergeConflictsAsync(string branch);

    /// <summary>
    /// Rebases a feature branch onto develop. Returns the new HEAD SHA.
    /// On conflict, aborts and throws <see cref="MergeConflictException"/>.
    /// </summary>
    Task<string> RebaseAsync(string branch);
}
