using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Merge, rebase, revert, and conflict detection operations.
/// All methods serialize via <see cref="_gitLock"/> inherited from the primary declaration.
/// </summary>
public partial class GitService
{
    // ── Merge & Revert ──────────────────────────────────────────

    /// <summary>
    /// Squash-merges a task branch into the current branch (develop).
    /// Uses <c>git reset --hard HEAD</c> on failure to cleanly undo squash state.
    /// When <paramref name="author"/> is provided, the merge commit uses <c>--author</c>.
    /// </summary>
    public async Task<string> SquashMergeAsync(string branch, string commitMessage, AgentGitIdentity? author = null)
    {
        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("checkout", "develop");
            try
            {
                await RunGitAsync("merge", "--squash", branch);
                await RunGitAsync("add", "-A");

                if (author is not null
                    && !string.IsNullOrWhiteSpace(author.AuthorName)
                    && !string.IsNullOrWhiteSpace(author.AuthorEmail))
                {
                    var authorArg = $"{author.AuthorName} <{author.AuthorEmail}>";
                    await RunGitAsync("commit", "-m", commitMessage, "--author", authorArg);
                }
                else
                {
                    await RunGitAsync("commit", "-m", commitMessage);
                }

                var mergeCommitSha = await RunGitAsync("rev-parse", "HEAD");
                _logger.LogInformation("Squash-merged {Branch} into develop (author: {Author})",
                    branch, author?.AuthorName ?? "default");
                return mergeCommitSha;
            }
            catch
            {
                _logger.LogWarning("Merge failed for {Branch}, resetting to HEAD", branch);
                try { await RunGitAsync("reset", "--hard", "HEAD"); }
                catch { /* best effort */ }
                throw;
            }
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Reverts a commit on develop. Used by REJECT_TASK to undo a squash-merge.
    /// Returns the SHA of the revert commit.
    /// </summary>
    public async Task<string> RevertCommitAsync(string commitSha)
    {
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new ArgumentException("Commit SHA is required.", nameof(commitSha));

        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("checkout", "develop");
            await RunGitAsync("revert", "--no-edit", commitSha);
            var revertSha = await RunGitAsync("rev-parse", "HEAD");
            _logger.LogInformation("Reverted commit {CommitSha} on develop → {RevertSha}", commitSha, revertSha);
            return revertSha;
        }
        catch
        {
            _logger.LogWarning("Revert failed for {CommitSha}, aborting revert", commitSha);
            try { await RunGitAsync("revert", "--abort"); }
            catch { /* best effort */ }
            throw;
        }
        finally
        {
            _gitLock.Release();
        }
    }

    // ── Rebase & Conflict Detection ─────────────────────────────

    /// <summary>
    /// Performs a dry-run merge to detect conflicts between develop and a feature branch
    /// without modifying the working tree. Returns the list of conflicting files.
    /// </summary>
    public virtual async Task<MergeConflictResult> DetectMergeConflictsAsync(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new ArgumentException("Branch name is required.", nameof(branch));

        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("checkout", "develop");

            try
            {
                await RunGitAsync("merge", "--no-commit", "--no-ff", branch);
                // No conflicts — abort the uncommitted merge
                try { await RunGitAsync("merge", "--abort"); }
                catch { /* merge --abort fails if merge completed cleanly; reset instead */ }
                try { await RunGitAsync("reset", "--hard", "HEAD"); }
                catch { /* best effort */ }
                return new MergeConflictResult(false, []);
            }
            catch (InvalidOperationException ex)
            {
                // Check if this is a real conflict or a different failure
                var conflictFiles = await GetConflictedFileListAsync();

                try { await RunGitAsync("merge", "--abort"); }
                catch
                {
                    _logger.LogWarning("merge --abort failed for {Branch}, attempting hard reset", branch);
                    try { await RunGitAsync("reset", "--hard", "HEAD"); }
                    catch (Exception resetEx)
                    {
                        _logger.LogError(resetEx, "Hard reset failed after merge abort failure on {Branch}", branch);
                    }
                }

                if (conflictFiles.Count > 0)
                    return new MergeConflictResult(true, conflictFiles);

                // Merge failed for a non-conflict reason — propagate the original error
                throw new InvalidOperationException(
                    $"Merge conflict check failed for branch '{branch}': {ex.Message}", ex);
            }
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Rebases a feature branch onto develop. Returns the new HEAD SHA of the rebased branch.
    /// On conflict, aborts the rebase, detects conflicting files, and throws
    /// <see cref="MergeConflictException"/> with details.
    /// </summary>
    public virtual async Task<string> RebaseAsync(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new ArgumentException("Branch name is required.", nameof(branch));
        if (branch is "develop" or "main")
            throw new InvalidOperationException($"Cannot rebase protected branch '{branch}'");

        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("checkout", branch);
            try
            {
                await RunGitAsync("rebase", "develop");
                var newHead = await RunGitAsync("rev-parse", "HEAD");
                _logger.LogInformation("Rebased {Branch} onto develop → {NewHead}", branch, newHead);

                // Return to develop after successful rebase
                await RunGitAsync("checkout", "develop");
                return newHead;
            }
            catch (InvalidOperationException ex)
            {
                // Rebase failed — check if it's a conflict or a different failure
                var conflictFiles = await GetConflictedFileListAsync();

                // Abort rebase, then hard-reset as fallback, then return to develop
                try { await RunGitAsync("rebase", "--abort"); }
                catch
                {
                    _logger.LogWarning("rebase --abort failed for {Branch}, attempting hard reset", branch);
                    try { await RunGitAsync("reset", "--hard", "HEAD"); }
                    catch (Exception resetEx)
                    {
                        _logger.LogError(resetEx, "Hard reset failed after rebase abort failure on {Branch}", branch);
                    }
                }

                try { await RunGitAsync("checkout", "develop"); }
                catch (Exception checkoutEx)
                {
                    _logger.LogError(checkoutEx,
                        "Failed to return to develop after rebase failure on {Branch} — repo may be in inconsistent state",
                        branch);
                }

                if (conflictFiles.Count > 0)
                    throw new MergeConflictException(branch, conflictFiles);

                // Non-conflict rebase failure — propagate the original error
                throw new InvalidOperationException(
                    $"Rebase failed for branch '{branch}': {ex.Message}", ex);
            }
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Reads the list of conflicted files from the git index during
    /// an active merge or rebase conflict.
    /// </summary>
    private async Task<List<string>> GetConflictedFileListAsync()
    {
        try
        {
            var diffOutput = await RunGitAsync("diff", "--name-only", "--diff-filter=U");
            return diffOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
