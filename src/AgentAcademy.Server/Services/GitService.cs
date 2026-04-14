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
public partial class GitService
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
    /// Pushes a branch to the remote origin. Used before creating a PR.
    /// </summary>
    public virtual async Task PushBranchAsync(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new ArgumentException("Branch name is required.", nameof(branch));

        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("push", "--set-upstream", "origin", branch);
            _logger.LogInformation("Pushed branch {Branch} to origin", branch);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Checks whether a local branch exists.
    /// </summary>
    public async Task<bool> BranchExistsAsync(string branch)
    {
        await _gitLock.WaitAsync();
        try
        {
            await RunGitAsync("rev-parse", "--verify", branch);
            return true;
        }
        catch
        {
            return false;
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

    private async Task<string> RunGitAsync(params string[] args)
        => await RunGitInDirInternalAsync(_repositoryRoot, args);

    public async Task<string> RunGitInDirAsync(string workingDir, params string[] args)
    {
        await _gitLock.WaitAsync();
        try { return await RunGitInDirInternalAsync(workingDir, args); }
        finally { _gitLock.Release(); }
    }

    public async Task<string> CommitInDirAsync(string workingDir, string message, AgentGitIdentity? author = null)
    {
        await _gitLock.WaitAsync();
        try
        {
            await RunGitInDirInternalAsync(workingDir, "add", "-A");
            if (author is not null && !string.IsNullOrWhiteSpace(author.AuthorName) && !string.IsNullOrWhiteSpace(author.AuthorEmail))
            {
                var authorArg = $"{author.AuthorName} <{author.AuthorEmail}>";
                await RunGitInDirInternalAsync(workingDir, "commit", "-m", message, "--author", authorArg);
            }
            else
            {
                await RunGitInDirInternalAsync(workingDir, "commit", "-m", message);
            }
            var sha = await RunGitInDirInternalAsync(workingDir, "rev-parse", "HEAD");
            _logger.LogInformation("Created commit {Sha} in {Dir} (author: {Author})", sha, workingDir, author?.AuthorName ?? "default");
            return sha;
        }
        finally { _gitLock.Release(); }
    }

    public async Task<string?> GetCurrentBranchInDirAsync(string workingDir)
    {
        await _gitLock.WaitAsync();
        try
        {
            var branch = await RunGitInDirInternalAsync(workingDir, "rev-parse", "--abbrev-ref", "HEAD");
            return string.IsNullOrWhiteSpace(branch) ? null : branch;
        }
        catch { return null; }
        finally { _gitLock.Release(); }
    }

    private async Task<string> RunGitInDirInternalAsync(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        _logger.LogDebug("Running in {Dir}: {Git} {Args}", workingDir, _gitExecutable, string.Join(" ", args));
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git process");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var message = $"git {string.Join(" ", args)} failed in {workingDir} (exit {process.ExitCode}): {stderr.Trim()}";
            _logger.LogWarning("{Message}", message);
            throw new InvalidOperationException(message);
        }
        return stdout.Trim();
    }

    /// <summary>
    /// Returns the list of files changed in a commit (relative paths).
    /// Uses git diff-tree to avoid parsing formatted log output.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetFilesInCommitAsync(string commitSha, string? workingDir = null)
    {
        var dir = workingDir ?? _repositoryRoot;
        await _gitLock.WaitAsync();
        try
        {
            var output = await RunGitInDirInternalAsync(dir, "diff-tree", "--no-commit-id", "--name-only", "-r", commitSha.Trim());
            if (string.IsNullOrWhiteSpace(output))
                return [];

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get files in commit {Sha}", commitSha);
            return [];
        }
        finally { _gitLock.Release(); }
    }

    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
