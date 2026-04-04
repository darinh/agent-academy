using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Git branch management service with round-scoped locking for breakout rooms.
/// All git operations are serialized via <see cref="_gitLock"/> to prevent
/// concurrent branch switches from corrupting the working tree.
/// </summary>
public class GitService
{
    private readonly ILogger<GitService> _logger;
    private readonly string _repositoryRoot;
    private readonly string _gitExecutable;
    private readonly SemaphoreSlim _gitLock = new(1, 1);
    private readonly SemaphoreSlim _roundLock = new(1, 1);

    public GitService(
        ILogger<GitService> logger,
        string? repositoryRoot = null,
        string gitExecutable = "git")
    {
        _logger = logger;
        _repositoryRoot = repositoryRoot ?? FindProjectRoot();
        _gitExecutable = string.IsNullOrWhiteSpace(gitExecutable) ? "git" : gitExecutable;
    }

    // ── Round-Scoped Locking ────────────────────────────────────

    /// <summary>
    /// Acquires the round lock. Held during the entire agent execution round
    /// so branch switches are safe from concurrent orchestrator activity.
    /// </summary>
    public async Task AcquireRoundLockAsync()
    {
        await _roundLock.WaitAsync();
        _logger.LogDebug("Round lock acquired");
    }

    /// <summary>
    /// Releases the round lock after the agent execution round completes.
    /// </summary>
    public void ReleaseRoundLock()
    {
        _roundLock.Release();
        _logger.LogDebug("Round lock released");
    }

    // ── Branch Operations ───────────────────────────────────────

    /// <summary>
    /// Creates a task branch from develop with a unique suffix to avoid collisions.
    /// Returns the branch name (e.g. "task/my-feature-a1b2c3").
    /// </summary>
    public virtual async Task<string> CreateTaskBranchAsync(string slug)
    {
        var sanitized = Regex.Replace(slug.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var branchName = $"task/{sanitized}-{suffix}";

        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("checkout", "develop");
            await RunGitAsync("checkout", "-b", branchName);
            _logger.LogInformation("Created task branch {Branch}", branchName);
            return branchName;
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Switches to the specified branch. Stashes any uncommitted changes
    /// (including untracked files) with a colon-delimited name for later recovery.
    /// Does NOT acquire the git lock — caller must hold the round lock.
    /// </summary>
    public async Task EnsureBranchInternalAsync(string branch)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stashName = $"auto-stash:{branch}:{timestamp}";

        await RunGitAsync("stash", "push", "--include-untracked", "-m", stashName);
        await RunGitAsync("checkout", branch);
        await TryPopStashForBranchAsync(branch);
        _logger.LogDebug("Switched to branch {Branch}", branch);
    }

    /// <summary>
    /// Stashes current work and returns to develop.
    /// Does NOT acquire the git lock — caller holds the round lock.
    /// </summary>
    public async Task ReturnToDevelopInternalAsync(string branch)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stashName = $"auto-stash:{branch}:{timestamp}";

        await RunGitAsync("stash", "push", "--include-untracked", "-m", stashName);
        await RunGitAsync("checkout", "develop");
        _logger.LogDebug("Returned to develop from {Branch}", branch);
    }

    /// <summary>
    /// Self-locking convenience wrapper for returning to develop.
    /// </summary>
    public async Task ReturnToDevelopAsync(string branch)
    {
        await _gitLock.WaitAsync();
        try
        {
            await ReturnToDevelopInternalAsync(branch);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Checks out an existing branch.
    /// </summary>
    public async Task CheckoutBranchAsync(string branch)
    {
        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("checkout", branch);
            _logger.LogInformation("Checked out branch {Branch}", branch);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Creates a commit from staged changes and returns the new commit SHA.
    /// When <paramref name="author"/> is provided, the commit uses <c>--author</c>
    /// to attribute the work to the specified agent identity.
    /// </summary>
    public async Task<string> CommitAsync(string message, AgentGitIdentity? author = null)
    {
        await _gitLock.WaitAsync();
        try
        {
            if (author is not null
                && !string.IsNullOrWhiteSpace(author.AuthorName)
                && !string.IsNullOrWhiteSpace(author.AuthorEmail))
            {
                var authorArg = $"{author.AuthorName} <{author.AuthorEmail}>";
                await RunGitAsync("commit", "-m", message, "--author", authorArg);
            }
            else
            {
                await RunGitAsync("commit", "-m", message);
            }

            var commitSha = await RunGitAsync("rev-parse", "HEAD");
            _logger.LogInformation("Created commit {CommitSha} (author: {Author})",
                commitSha, author?.AuthorName ?? "default");
            return commitSha;
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Pops the most recent auto-stash for the given branch.
    /// Returns <c>true</c> when a matching stash is restored.
    /// </summary>
    public async Task<bool> PopAutoStashAsync(string branch)
    {
        await _gitLock.WaitAsync();
        try
        {
            var stashRef = await FindAutoStashRefAsync(branch);
            if (stashRef is null)
                return false;

            await RunGitAsync("stash", "pop", stashRef);
            _logger.LogInformation("Popped stash {StashRef} for branch {Branch}", stashRef, branch);
            return true;
        }
        finally
        {
            _gitLock.Release();
        }
    }

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
    /// Deletes a branch. Refuses to delete develop or main.
    /// </summary>
    public async Task DeleteBranchAsync(string branch)
    {
        if (branch is "develop" or "main")
            throw new InvalidOperationException($"Cannot delete protected branch '{branch}'");

        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("branch", "-D", branch);
            _logger.LogInformation("Deleted branch {Branch}", branch);
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
    /// Result of a conflict detection check.
    /// </summary>
    public record MergeConflictResult(bool HasConflicts, IReadOnlyList<string> ConflictingFiles);

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

    // ── Stash Helpers ───────────────────────────────────────────

    /// <summary>
    /// Pops the most recent stash whose name matches the branch pattern.
    /// Uses exact regex matching: <c>auto-stash:{branch}:\d+$</c>.
    /// </summary>
    private async Task<bool> TryPopStashForBranchAsync(string branch)
    {
        try
        {
            var stashRef = await FindAutoStashRefAsync(branch);
            if (stashRef is null)
                return false;

            await RunGitAsync("stash", "pop", stashRef);
            _logger.LogDebug("Popped stash for branch {Branch}", branch);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No stash to pop for branch {Branch}", branch);
        }

        return false;
    }

    private async Task<string?> FindAutoStashRefAsync(string branch)
    {
        var stashList = await RunGitAsync("stash", "list");
        var pattern = new Regex($@"auto-stash:{Regex.Escape(branch)}:\d+$");

        foreach (var line in stashList.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pattern.IsMatch(line))
                return line.Split(':')[0]; // e.g. "stash@{0}"
        }

        return null;
    }

    // ── Git Process Runner ──────────────────────────────────────

    /// <summary>
    /// Runs a git command using <see cref="ProcessStartInfo.ArgumentList"/>
    /// for safe argument passing (no string concatenation / shell injection).
    /// </summary>
    private async Task<string> RunGitAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = _repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        _logger.LogDebug("Running: {GitExecutable} {Args}", _gitExecutable, string.Join(" ", args));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var message = $"git {string.Join(" ", args)} failed (exit {process.ExitCode}): {stderr.Trim()}";
            _logger.LogWarning("{Message}", message);
            throw new InvalidOperationException(message);
        }

        return stdout.Trim();
    }

    /// <summary>
    /// Walks up from the current directory looking for <c>AgentAcademy.sln</c>
    /// to find the project root.
    /// </summary>
    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
