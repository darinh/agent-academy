using System.Diagnostics;
using AgentAcademy.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class GitServiceDirTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly GitService _service;
    private readonly List<string> _tempDirs = [];

    public GitServiceDirTests()
    {
        _repoRoot = CreateTempDir("gitservice-dir-test");
        InitializeRepository(_repoRoot);
        _service = new GitService(NullLogger<GitService>.Instance, _repoRoot, "git");
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { ForceDeleteDirectory(dir); } catch { }
    }

    [Fact]
    public async Task RunGitInDirAsync_RunsInSpecifiedDirectory()
    {
        var result = await _service.RunGitInDirAsync(_repoRoot, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("develop", result);
    }

    [Fact]
    public async Task RunGitInDirAsync_WorksInWorktreeDir()
    {
        var branch = CreateFeatureBranch("dir-wt", "dir.txt", "content");
        var wtPath = CreateWorktree(branch);
        var result = await _service.RunGitInDirAsync(wtPath, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal(branch, result);
    }

    [Fact]
    public async Task RunGitInDirAsync_ThrowsOnBadCommand()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RunGitInDirAsync(_repoRoot, "this-is-not-a-command"));
    }

    [Fact]
    public async Task CommitInDirAsync_CreatesCommitInWorktree()
    {
        var branch = CreateFeatureBranch("commit-dir", "c.txt", "v1");
        var wtPath = CreateWorktree(branch);
        File.WriteAllText(Path.Combine(wtPath, "c.txt"), "v2");
        var sha = await _service.CommitInDirAsync(wtPath, "Update via CommitInDirAsync");
        Assert.False(string.IsNullOrWhiteSpace(sha));
        var log = RunGit(wtPath, "log", "--oneline", "-1");
        Assert.Contains("Update via CommitInDirAsync", log);
        var devLog = RunGit(_repoRoot, "log", "--oneline", "-1");
        Assert.DoesNotContain("Update via CommitInDirAsync", devLog);
    }

    [Fact]
    public async Task CommitInDirAsync_SetsAuthorIdentity()
    {
        var branch = CreateFeatureBranch("commit-author", "a.txt", "v1");
        var wtPath = CreateWorktree(branch);
        File.WriteAllText(Path.Combine(wtPath, "a.txt"), "v2");
        var author = new AgentAcademy.Shared.Models.AgentGitIdentity("Test Agent", "agent@test.local");
        await _service.CommitInDirAsync(wtPath, "Authored commit", author);
        var log = RunGit(wtPath, "log", "--format=%an <%ae>", "-1");
        Assert.Contains("Test Agent", log);
        Assert.Contains("agent@test.local", log);
    }

    [Fact]
    public async Task GetCurrentBranchInDirAsync_ReturnsBranch()
    {
        var result = await _service.GetCurrentBranchInDirAsync(_repoRoot);
        Assert.Equal("develop", result);
    }

    [Fact]
    public async Task GetCurrentBranchInDirAsync_ReturnsWorktreeBranch()
    {
        var branch = CreateFeatureBranch("branch-dir", "b.txt", "x");
        var wtPath = CreateWorktree(branch);
        var result = await _service.GetCurrentBranchInDirAsync(wtPath);
        Assert.Equal(branch, result);
    }

    [Fact]
    public async Task GetCurrentBranchInDirAsync_ReturnsNullForNonGitDir()
    {
        var nonGitDir = CreateTempDir("non-git");
        var result = await _service.GetCurrentBranchInDirAsync(nonGitDir);
        Assert.Null(result);
    }

    private string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static void InitializeRepository(string repoRoot)
    {
        RunGit(repoRoot, "init");
        RunGit(repoRoot, "config", "user.name", "GitService Tests");
        RunGit(repoRoot, "config", "user.email", "tests@gitservice.local");
        RunGit(repoRoot, "checkout", "-b", "develop");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "init\n");
        RunGit(repoRoot, "add", "README.md");
        RunGit(repoRoot, "commit", "-m", "Initial commit");
    }

    private string CreateFeatureBranch(string name, string fileName, string content)
    {
        var branchName = $"task/{name}";
        RunGit(_repoRoot, "checkout", "-b", branchName);
        File.WriteAllText(Path.Combine(_repoRoot, fileName), content);
        RunGit(_repoRoot, "add", fileName);
        RunGit(_repoRoot, "commit", "-m", $"Add {fileName}");
        RunGit(_repoRoot, "checkout", "develop");
        return branchName;
    }

    private string CreateWorktree(string branch)
    {
        var wtDir = Path.Combine(_repoRoot, ".worktrees", branch.Replace("/", "_"));
        Directory.CreateDirectory(Path.GetDirectoryName(wtDir)!);
        RunGit(_repoRoot, "worktree", "add", wtDir, branch);
        _tempDirs.Add(wtDir);
        return wtDir;
    }

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git", WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr.Trim()}");
        return stdout.Trim();
    }

    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
