using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Git branch management service with round-scoped locking for breakout rooms.
/// All git operations are serialized via <see cref="_gitLock"/> to prevent
/// concurrent branch switches from corrupting the working tree.
/// </summary>
public sealed class GitService
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
    public async Task<string> CreateTaskBranchAsync(string slug)
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
    /// </summary>
    public async Task<string> CommitAsync(string message)
    {
        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("commit", "-m", message);
            var commitSha = await RunGitAsync("rev-parse", "HEAD");
            _logger.LogInformation("Created commit {CommitSha}", commitSha);
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
    /// Cleans up with <c>git merge --abort</c> on failure.
    /// </summary>
    public async Task<string> SquashMergeAsync(string branch, string commitMessage)
    {
        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("checkout", "develop");
            try
            {
                await RunGitAsync("merge", "--squash", branch);
                await RunGitAsync("add", "-A");
                await RunGitAsync("commit", "-m", commitMessage);
                var mergeCommitSha = await RunGitAsync("rev-parse", "HEAD");
                _logger.LogInformation("Squash-merged {Branch} into develop", branch);
                return mergeCommitSha;
            }
            catch
            {
                _logger.LogWarning("Merge failed for {Branch}, aborting", branch);
                try { await RunGitAsync("merge", "--abort"); }
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
