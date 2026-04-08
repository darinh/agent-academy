using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages git worktrees for task-level isolation. Each task gets its own
/// worktree directory so agents can work on different branches in parallel
/// without stash/checkout cycling on the main working tree.
/// </summary>
public class WorktreeService : IDisposable
{
    private readonly ILogger<WorktreeService> _logger;
    private readonly string _repositoryRoot;
    private readonly string _worktreeRoot;
    private readonly string _gitExecutable;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, WorktreeInfo> _activeWorktrees = new();
    private bool _disposed;

    public WorktreeService(
        ILogger<WorktreeService> logger,
        string? repositoryRoot = null,
        string gitExecutable = "git")
    {
        _logger = logger;
        _repositoryRoot = repositoryRoot ?? FindProjectRoot();
        _gitExecutable = string.IsNullOrWhiteSpace(gitExecutable) ? "git" : gitExecutable;
        _worktreeRoot = Path.Combine(_repositoryRoot, ".worktrees");
    }

    /// <summary>
    /// Creates a worktree for an existing branch, returning the worktree path.
    /// The branch must already exist (created by <see cref="GitService.CreateTaskBranchAsync"/>).
    /// If a worktree already exists for this branch, returns the existing path.
    /// </summary>
    public async Task<WorktreeInfo> CreateWorktreeAsync(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new ArgumentException("Branch name is required.", nameof(branch));

        if (_activeWorktrees.TryGetValue(branch, out var existing))
        {
            if (Directory.Exists(existing.Path))
            {
                _logger.LogDebug("Worktree already exists for {Branch} at {Path}", branch, existing.Path);
                return existing;
            }
            // Path gone — remove stale entry and recreate
            _activeWorktrees.TryRemove(branch, out _);
        }

        await _lock.WaitAsync();
        try
        {
            // Double-check after lock acquisition
            if (_activeWorktrees.TryGetValue(branch, out existing) && Directory.Exists(existing.Path))
                return existing;

            Directory.CreateDirectory(_worktreeRoot);

            var worktreePath = BuildWorktreePath(branch);

            // If the directory already exists from a previous unclean shutdown, remove it first
            if (Directory.Exists(worktreePath))
            {
                _logger.LogWarning("Stale worktree directory found at {Path}, cleaning up", worktreePath);
                await TryRemoveWorktreeGitAsync(worktreePath);
            }

            await RunGitAsync("worktree", "add", worktreePath, branch);
            _logger.LogInformation("Created worktree for {Branch} at {Path}", branch, worktreePath);

            var info = new WorktreeInfo(branch, worktreePath, DateTimeOffset.UtcNow);
            _activeWorktrees[branch] = info;
            return info;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes the worktree for a branch and cleans up the directory.
    /// Safe to call if no worktree exists for the branch.
    /// </summary>
    public async Task RemoveWorktreeAsync(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            return;

        await _lock.WaitAsync();
        try
        {
            _activeWorktrees.TryRemove(branch, out var info);

            var worktreePath = info?.Path ?? BuildWorktreePath(branch);

            await TryRemoveWorktreeGitAsync(worktreePath);
            await PruneWorktreesAsync();

            _logger.LogInformation("Removed worktree for {Branch}", branch);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns the worktree path for a branch, or null if no worktree is active.
    /// </summary>
    public string? GetWorktreePath(string branch)
    {
        if (_activeWorktrees.TryGetValue(branch, out var info) && Directory.Exists(info.Path))
            return info.Path;
        return null;
    }

    /// <summary>
    /// Returns all currently tracked worktrees.
    /// </summary>
    public IReadOnlyList<WorktreeInfo> GetActiveWorktrees()
        => _activeWorktrees.Values.ToList().AsReadOnly();

    /// <summary>
    /// Lists worktrees reported by git (not just our tracked ones).
    /// Useful for detecting orphans from previous runs.
    /// </summary>
    public async Task<IReadOnlyList<GitWorktreeEntry>> ListGitWorktreesAsync()
    {
        var output = await RunGitAsync("worktree", "list", "--porcelain");
        return ParseWorktreeList(output);
    }

    /// <summary>
    /// Removes all worktrees under the worktree root and prunes git metadata.
    /// Used during shutdown or error recovery.
    /// </summary>
    public async Task CleanupAllWorktreesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var branches = _activeWorktrees.Keys.ToList();
            foreach (var branch in branches)
            {
                _activeWorktrees.TryRemove(branch, out var info);
                if (info is not null)
                    await TryRemoveWorktreeGitAsync(info.Path);
            }

            // Also scan for any git knows about that we don't track
            var entries = await ListGitWorktreesAsync();
            foreach (var entry in entries)
            {
                if (IsUnderWorktreeRoot(entry.Path)
                    && entry.Path != _repositoryRoot)
                {
                    await TryRemoveWorktreeGitAsync(entry.Path);
                }
            }

            await PruneWorktreesAsync();

            // Clean up the worktree root directory itself if empty
            if (Directory.Exists(_worktreeRoot) && !Directory.EnumerateFileSystemEntries(_worktreeRoot).Any())
                Directory.Delete(_worktreeRoot);

            _logger.LogInformation("Cleaned up all worktrees");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Syncs the in-memory tracking dictionary with what git actually has.
    /// Adds entries for worktrees git knows about under our root, removes entries
    /// for worktrees that no longer exist on disk.
    /// </summary>
    public async Task SyncWithGitAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Remove stale tracked entries
            foreach (var (branch, info) in _activeWorktrees)
            {
                if (!Directory.Exists(info.Path))
                    _activeWorktrees.TryRemove(branch, out _);
            }

            // Add untracked worktrees that git knows about
            var entries = await ListGitWorktreesAsync();
            foreach (var entry in entries)
            {
                if (entry.Branch is not null
                    && IsUnderWorktreeRoot(entry.Path)
                    && !_activeWorktrees.ContainsKey(entry.Branch))
                {
                    _activeWorktrees[entry.Branch] = new WorktreeInfo(
                        entry.Branch, entry.Path, DateTimeOffset.UtcNow);
                    _logger.LogDebug("Recovered untracked worktree for {Branch} at {Path}",
                        entry.Branch, entry.Path);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Internal Helpers ────────────────────────────────────────

    /// <summary>
    /// Builds a collision-resistant worktree directory path by combining
    /// a human-readable prefix with a short hash of the original branch name.
    /// </summary>
    private string BuildWorktreePath(string branch)
    {
        var safeName = Regex.Replace(branch, @"[^a-zA-Z0-9\-]", "_");
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(branch)))[..8].ToLowerInvariant();
        return Path.Combine(_worktreeRoot, $"{safeName}-{hash}");
    }

    /// <summary>
    /// Checks that <paramref name="candidatePath"/> is a direct child of the worktree root,
    /// not just a prefix match (prevents /root-backup/x matching /root).
    /// </summary>
    private bool IsUnderWorktreeRoot(string candidatePath)
    {
        var fullCandidate = Path.GetFullPath(candidatePath);
        var fullRoot = Path.GetFullPath(_worktreeRoot);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        return !relative.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private async Task TryRemoveWorktreeGitAsync(string worktreePath)
    {
        try
        {
            await RunGitAsync("worktree", "remove", "--force", worktreePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "git worktree remove failed for {Path}, falling back to directory delete", worktreePath);
            try
            {
                if (Directory.Exists(worktreePath))
                    Directory.Delete(worktreePath, recursive: true);
            }
            catch (Exception dirEx)
            {
                _logger.LogWarning(dirEx, "Failed to delete worktree directory {Path}", worktreePath);
            }
        }
    }

    private async Task PruneWorktreesAsync()
    {
        try
        {
            await RunGitAsync("worktree", "prune");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "git worktree prune failed (non-fatal)");
        }
    }

    internal static IReadOnlyList<GitWorktreeEntry> ParseWorktreeList(string porcelainOutput)
    {
        var entries = new List<GitWorktreeEntry>();
        string? path = null;
        string? head = null;
        string? branch = null;
        bool bare = false;

        foreach (var line in porcelainOutput.Split('\n'))
        {
            if (line.StartsWith("worktree "))
            {
                // Save previous entry if any
                if (path is not null)
                    entries.Add(new GitWorktreeEntry(path, head, branch, bare));

                path = line["worktree ".Length..];
                head = null;
                branch = null;
                bare = false;
            }
            else if (line.StartsWith("HEAD "))
            {
                head = line["HEAD ".Length..];
            }
            else if (line.StartsWith("branch "))
            {
                var fullRef = line["branch ".Length..];
                branch = fullRef.StartsWith("refs/heads/")
                    ? fullRef["refs/heads/".Length..]
                    : fullRef;
            }
            else if (line == "bare")
            {
                bare = true;
            }
        }

        // Don't forget the last entry
        if (path is not null)
            entries.Add(new GitWorktreeEntry(path, head, branch, bare));

        return entries.AsReadOnly();
    }

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

        // Read stdout and stderr concurrently to avoid deadlock when buffers fill
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        if (process.ExitCode != 0)
        {
            var message = $"git {string.Join(" ", args)} failed (exit {process.ExitCode}): {stderr.Trim()}";
            _logger.LogWarning("{Message}", message);
            throw new InvalidOperationException(message);
        }

        return stdout.Trim();
    }

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
    }
}

/// <summary>
/// Tracks an active worktree managed by <see cref="WorktreeService"/>.
/// </summary>
public record WorktreeInfo(string Branch, string Path, DateTimeOffset CreatedAt);

/// <summary>
/// Represents a worktree entry as reported by <c>git worktree list --porcelain</c>.
/// </summary>
public record GitWorktreeEntry(string Path, string? Head, string? Branch, bool Bare);
