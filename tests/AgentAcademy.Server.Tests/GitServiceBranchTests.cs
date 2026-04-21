using System.Diagnostics;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class GitServiceBranchTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly GitService _service;
    private readonly List<string> _tempDirs = [];

    public GitServiceBranchTests()
    {
        _repoRoot = CreateTempDir("gitservice-branch-test");
        InitializeRepository(_repoRoot);
        _service = new GitService(NullLogger<GitService>.Instance, _repoRoot, "git");
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { ForceDeleteDirectory(dir); } catch { }
    }

    // ── CreateTaskBranchAsync ───────────────────────────────────

    [Fact]
    public async Task CreateTaskBranchAsync_ReturnsFormattedBranchName()
    {
        var branch = await _service.CreateTaskBranchAsync("my-feature");
        Assert.StartsWith("task/", branch);
    }

    [Fact]
    public async Task CreateTaskBranchAsync_SanitizesSlug()
    {
        var branch = await _service.CreateTaskBranchAsync("Hello World!! @Special#Chars");
        // Should be lowercased, special chars replaced with hyphens
        Assert.StartsWith("task/hello-world-special-chars-", branch);
    }

    [Fact]
    public async Task CreateTaskBranchAsync_BranchExistsAfterCreation()
    {
        var branch = await _service.CreateTaskBranchAsync("exists-check");
        var branches = RunGit(_repoRoot, "branch", "--list");
        Assert.Contains(branch, branches);
    }

    [Fact]
    public async Task CreateTaskBranchAsync_LeavesOnNewBranch()
    {
        var branch = await _service.CreateTaskBranchAsync("leave-on-new");
        var current = RunGit(_repoRoot, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal(branch, current);
    }

    [Fact]
    public async Task CreateTaskBranchAsync_BranchContainsSuffix()
    {
        var branch = await _service.CreateTaskBranchAsync("suffix-test");
        // Format is task/{sanitized}-{6-char-hex}
        var afterPrefix = branch["task/".Length..];
        var suffix = afterPrefix.Split('-')[^1];
        Assert.Equal(6, suffix.Length);
        Assert.Matches("^[0-9a-f]{6}$", suffix);
    }

    // ── EnsureBranchInternalAsync ───────────────────────────────

    [Fact]
    public async Task EnsureBranchInternalAsync_SwitchesToBranch()
    {
        var branch = CreateFeatureBranch("ensure-switch", "ensure.txt", "data");
        await _service.EnsureBranchInternalAsync(branch);
        var current = RunGit(_repoRoot, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal(branch, current);
    }

    [Fact]
    public async Task EnsureBranchInternalAsync_PreservesUncommittedChanges()
    {
        var branch = CreateFeatureBranch("ensure-stash", "stash.txt", "original");

        // Create an uncommitted file on develop
        var trackedFile = Path.Combine(_repoRoot, "uncommitted.txt");
        File.WriteAllText(trackedFile, "uncommitted work");
        RunGit(_repoRoot, "add", "uncommitted.txt");

        // Switch to feature branch (stashes uncommitted work)
        await _service.EnsureBranchInternalAsync(branch);
        Assert.Equal(branch, RunGit(_repoRoot, "rev-parse", "--abbrev-ref", "HEAD"));

        // Switch back to develop (stashes feature branch state)
        await _service.EnsureBranchInternalAsync("develop");

        // Pop the auto-stash for develop to recover the uncommitted file
        await _service.PopAutoStashAsync("develop");
        Assert.True(File.Exists(trackedFile));
        Assert.Equal("uncommitted work", File.ReadAllText(trackedFile));
    }

    // ── ReturnToDevelopInternalAsync ────────────────────────────

    [Fact]
    public async Task ReturnToDevelopInternalAsync_SwitchesToDevelop()
    {
        var branch = CreateFeatureBranch("return-internal", "ret.txt", "data");
        RunGit(_repoRoot, "checkout", branch);
        await _service.ReturnToDevelopInternalAsync(branch);
        var current = RunGit(_repoRoot, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("develop", current);
    }

    // ── ReturnToDevelopAsync ────────────────────────────────────

    [Fact]
    public async Task ReturnToDevelopAsync_SwitchesToDevelop()
    {
        var branch = CreateFeatureBranch("return-locked", "rl.txt", "data");
        RunGit(_repoRoot, "checkout", branch);
        await _service.ReturnToDevelopAsync(branch);
        var current = RunGit(_repoRoot, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("develop", current);
    }

    // ── CheckoutBranchAsync ─────────────────────────────────────

    [Fact]
    public async Task CheckoutBranchAsync_SwitchesToBranch()
    {
        var branch = CreateFeatureBranch("checkout-switch", "co.txt", "data");
        await _service.CheckoutBranchAsync(branch);
        var current = RunGit(_repoRoot, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal(branch, current);
    }

    [Fact]
    public async Task CheckoutBranchAsync_ThrowsForNonexistentBranch()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CheckoutBranchAsync("nonexistent-branch-xyz"));
    }

    // ── CommitAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_CreatesCommit()
    {
        var filePath = Path.Combine(_repoRoot, "commit-test.txt");
        File.WriteAllText(filePath, "test content");
        RunGit(_repoRoot, "add", "commit-test.txt");

        await _service.CommitAsync("feat: test commit");

        var log = RunGit(_repoRoot, "log", "--oneline", "-1");
        Assert.Contains("feat: test commit", log);
    }

    [Fact]
    public async Task CommitAsync_ReturnsCommitSha()
    {
        var filePath = Path.Combine(_repoRoot, "sha-test.txt");
        File.WriteAllText(filePath, "sha content");
        RunGit(_repoRoot, "add", "sha-test.txt");

        var sha = await _service.CommitAsync("feat: sha test");

        Assert.False(string.IsNullOrWhiteSpace(sha));
        Assert.Matches("^[0-9a-f]{40}$", sha);
    }

    [Fact]
    public async Task CommitAsync_WithAuthor_SetsAuthorIdentity()
    {
        var filePath = Path.Combine(_repoRoot, "author-test.txt");
        File.WriteAllText(filePath, "authored content");
        RunGit(_repoRoot, "add", "author-test.txt");

        var author = new AgentGitIdentity("Bot Agent", "bot@agent.local");
        await _service.CommitAsync("feat: authored commit", author);

        var authorName = RunGit(_repoRoot, "log", "--format=%an", "-1");
        var authorEmail = RunGit(_repoRoot, "log", "--format=%ae", "-1");
        Assert.Equal("Bot Agent", authorName);
        Assert.Equal("bot@agent.local", authorEmail);
    }

    [Fact]
    public async Task CommitAsync_WithoutAuthor_UsesDefault()
    {
        var filePath = Path.Combine(_repoRoot, "default-author.txt");
        File.WriteAllText(filePath, "default content");
        RunGit(_repoRoot, "add", "default-author.txt");

        await _service.CommitAsync("feat: default author");

        var authorName = RunGit(_repoRoot, "log", "--format=%an", "-1");
        Assert.Equal("GitService Tests", authorName);
    }

    // ── BranchExistsAsync ───────────────────────────────────────

    [Fact]
    public async Task BranchExistsAsync_ReturnsTrueForExistingBranch()
    {
        var branch = CreateFeatureBranch("exists-true", "e.txt", "data");
        Assert.True(await _service.BranchExistsAsync(branch));
    }

    [Fact]
    public async Task BranchExistsAsync_ReturnsFalseForNonexistentBranch()
    {
        Assert.False(await _service.BranchExistsAsync("nonexistent-branch-abc123"));
    }

    [Fact]
    public async Task BranchExistsAsync_ReturnsTrueForDevelop()
    {
        Assert.True(await _service.BranchExistsAsync("develop"));
    }

    // ── DeleteBranchAsync ───────────────────────────────────────

    [Fact]
    public async Task DeleteBranchAsync_DeletesBranch()
    {
        var branch = CreateFeatureBranch("delete-me", "del.txt", "data");
        Assert.True(await _service.BranchExistsAsync(branch));

        await _service.DeleteBranchAsync(branch);

        Assert.False(await _service.BranchExistsAsync(branch));
    }

    [Fact]
    public async Task DeleteBranchAsync_ThrowsForDevelop()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteBranchAsync("develop"));
        Assert.Contains("protected branch", ex.Message);
    }

    [Fact]
    public async Task DeleteBranchAsync_ThrowsForMain()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteBranchAsync("main"));
        Assert.Contains("protected branch", ex.Message);
    }

    [Fact]
    public async Task DeleteBranchAsync_ThrowsForNonexistentBranch()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.DeleteBranchAsync("nonexistent-branch-xyz"));
    }

    // ── PushBranchAsync ─────────────────────────────────────────

    [Fact]
    public async Task PushBranchAsync_ThrowsForEmptyBranch()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.PushBranchAsync(""));
    }

    [Fact]
    public async Task PushBranchAsync_ThrowsForWhitespaceBranch()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.PushBranchAsync("   "));
    }

    // ── PopAutoStashAsync ───────────────────────────────────────

    [Fact]
    public async Task PopAutoStashAsync_ReturnsFalseWhenNoStash()
    {
        var result = await _service.PopAutoStashAsync("develop");
        Assert.False(result);
    }

    [Fact]
    public async Task PopAutoStashAsync_ReturnsTrueWhenStashExists()
    {
        var branch = CreateFeatureBranch("stash-pop", "sp.txt", "data");

        // Switch to feature branch first
        await _service.CheckoutBranchAsync(branch);

        // Create an uncommitted change on the feature branch
        File.WriteAllText(Path.Combine(_repoRoot, "stashed-work.txt"), "stashed content");
        RunGit(_repoRoot, "add", "stashed-work.txt");

        // ReturnToDevelopInternalAsync stashes with name auto-stash:{branch}:{ts}
        await _service.ReturnToDevelopInternalAsync(branch);
        Assert.Equal("develop", RunGit(_repoRoot, "rev-parse", "--abbrev-ref", "HEAD"));

        // Pop the auto-stash for the feature branch (the stash is named after the branch arg)
        var result = await _service.PopAutoStashAsync(branch);
        Assert.True(result);
        Assert.True(File.Exists(Path.Combine(_repoRoot, "stashed-work.txt")));
    }

    // ── SquashMergeAsync ────────────────────────────────────────

    [Fact]
    public async Task SquashMergeAsync_MergesBranchIntoDevelop()
    {
        var branch = CreateFeatureBranch("squash-merge", "squash.txt", "squash content");

        var sha = await _service.SquashMergeAsync(branch, "feat: squash merge");

        Assert.False(string.IsNullOrWhiteSpace(sha));
        // File from feature branch should appear on develop
        Assert.True(File.Exists(Path.Combine(_repoRoot, "squash.txt")));
        Assert.Equal("squash content", File.ReadAllText(Path.Combine(_repoRoot, "squash.txt")));
    }

    [Fact]
    public async Task SquashMergeAsync_ReturnsCommitSha()
    {
        var branch = CreateFeatureBranch("squash-sha", "sq-sha.txt", "data");

        var sha = await _service.SquashMergeAsync(branch, "feat: squash sha");

        Assert.Matches("^[0-9a-f]{40}$", sha);
    }

    [Fact]
    public async Task SquashMergeAsync_WithAuthor_SetsAuthorIdentity()
    {
        var branch = CreateFeatureBranch("squash-author", "sq-auth.txt", "data");
        var author = new AgentGitIdentity("Merge Bot", "merge@bot.local");

        await _service.SquashMergeAsync(branch, "feat: squash with author", author);

        var authorName = RunGit(_repoRoot, "log", "--format=%an", "-1");
        var authorEmail = RunGit(_repoRoot, "log", "--format=%ae", "-1");
        Assert.Equal("Merge Bot", authorName);
        Assert.Equal("merge@bot.local", authorEmail);
    }

    [Fact]
    public async Task SquashMergeAsync_LeavesOnDevelop()
    {
        var branch = CreateFeatureBranch("squash-develop", "sq-dev.txt", "data");

        await _service.SquashMergeAsync(branch, "feat: squash leaves develop");

        var current = RunGit(_repoRoot, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("develop", current);
    }

    // ── RevertCommitAsync ───────────────────────────────────────

    [Fact]
    public async Task RevertCommitAsync_RevertsTheCommit()
    {
        // Create a commit with a new file on develop
        File.WriteAllText(Path.Combine(_repoRoot, "revert-me.txt"), "will be reverted");
        RunGit(_repoRoot, "add", "revert-me.txt");
        RunGit(_repoRoot, "commit", "-m", "Add revert-me.txt");
        var commitSha = RunGit(_repoRoot, "rev-parse", "HEAD");

        await _service.RevertCommitAsync(commitSha);

        Assert.False(File.Exists(Path.Combine(_repoRoot, "revert-me.txt")));
    }

    [Fact]
    public async Task RevertCommitAsync_ReturnsRevertSha()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "revert-sha.txt"), "revert sha test");
        RunGit(_repoRoot, "add", "revert-sha.txt");
        RunGit(_repoRoot, "commit", "-m", "Add revert-sha.txt");
        var commitSha = RunGit(_repoRoot, "rev-parse", "HEAD");

        var revertSha = await _service.RevertCommitAsync(commitSha);

        Assert.Matches("^[0-9a-f]{40}$", revertSha);
        Assert.NotEqual(commitSha, revertSha);
    }

    [Fact]
    public async Task RevertCommitAsync_ThrowsForEmptySha()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.RevertCommitAsync(""));
    }

    [Fact]
    public async Task RevertCommitAsync_ThrowsForInvalidSha()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RevertCommitAsync("0000000000000000000000000000000000000000"));
    }

    // ── DetectMergeConflictsAsync ────────────────────────────────

    [Fact]
    public async Task DetectMergeConflictsAsync_NoConflicts_ReturnsFalse()
    {
        var branch = CreateFeatureBranch("no-conflict", "nc.txt", "no conflict");

        var result = await _service.DetectMergeConflictsAsync(branch);

        Assert.False(result.HasConflicts);
        Assert.Empty(result.ConflictingFiles);
    }

    [Fact]
    public async Task DetectMergeConflictsAsync_WithConflicts_ReturnsTrueAndListsFiles()
    {
        // Create a shared file on develop
        File.WriteAllText(Path.Combine(_repoRoot, "conflict.txt"), "base content");
        RunGit(_repoRoot, "add", "conflict.txt");
        RunGit(_repoRoot, "commit", "-m", "Add conflict.txt base");

        // Create a branch that modifies the file
        RunGit(_repoRoot, "checkout", "-b", "task/conflict-test");
        File.WriteAllText(Path.Combine(_repoRoot, "conflict.txt"), "branch version");
        RunGit(_repoRoot, "add", "conflict.txt");
        RunGit(_repoRoot, "commit", "-m", "Modify conflict.txt on branch");

        // Modify the same file differently on develop
        RunGit(_repoRoot, "checkout", "develop");
        File.WriteAllText(Path.Combine(_repoRoot, "conflict.txt"), "develop version");
        RunGit(_repoRoot, "add", "conflict.txt");
        RunGit(_repoRoot, "commit", "-m", "Modify conflict.txt on develop");

        var result = await _service.DetectMergeConflictsAsync("task/conflict-test");

        Assert.True(result.HasConflicts);
        Assert.Contains("conflict.txt", result.ConflictingFiles);
    }

    [Fact]
    public async Task DetectMergeConflictsAsync_ThrowsForEmptyBranch()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.DetectMergeConflictsAsync(""));
    }

    [Fact]
    public async Task DetectMergeConflictsAsync_LeavesOnDevelop()
    {
        var branch = CreateFeatureBranch("detect-develop", "dd.txt", "data");

        await _service.DetectMergeConflictsAsync(branch);

        var current = RunGit(_repoRoot, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("develop", current);
    }

    // ── RebaseAsync ─────────────────────────────────────────────

    [Fact]
    public async Task RebaseAsync_RebasesOnDevelop()
    {
        var branch = CreateFeatureBranch("rebase-ok", "rb.txt", "rebase content");

        // Add a new commit to develop so there's something to rebase onto
        File.WriteAllText(Path.Combine(_repoRoot, "develop-new.txt"), "new develop content");
        RunGit(_repoRoot, "add", "develop-new.txt");
        RunGit(_repoRoot, "commit", "-m", "New develop commit");

        var newHead = await _service.RebaseAsync(branch);

        Assert.Matches("^[0-9a-f]{40}$", newHead);

        // Verify the rebased branch contains both the feature commit and develop's new commit
        RunGit(_repoRoot, "checkout", branch);
        var log = RunGit(_repoRoot, "log", "--oneline");
        Assert.Contains("Add rb.txt", log);
        Assert.Contains("New develop commit", log);
    }

    [Fact]
    public async Task RebaseAsync_ThrowsForDevelop()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RebaseAsync("develop"));
        Assert.Contains("protected branch", ex.Message);
    }

    [Fact]
    public async Task RebaseAsync_ThrowsForMain()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RebaseAsync("main"));
        Assert.Contains("protected branch", ex.Message);
    }

    [Fact]
    public async Task RebaseAsync_ThrowsForEmptyBranch()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.RebaseAsync(""));
    }

    [Fact]
    public async Task RebaseAsync_WithConflicts_ThrowsMergeConflictException()
    {
        // Create a shared file on develop
        File.WriteAllText(Path.Combine(_repoRoot, "rebase-conflict.txt"), "base");
        RunGit(_repoRoot, "add", "rebase-conflict.txt");
        RunGit(_repoRoot, "commit", "-m", "Add rebase-conflict.txt base");

        // Create a branch that modifies the file
        RunGit(_repoRoot, "checkout", "-b", "task/rebase-conflict");
        File.WriteAllText(Path.Combine(_repoRoot, "rebase-conflict.txt"), "branch change");
        RunGit(_repoRoot, "add", "rebase-conflict.txt");
        RunGit(_repoRoot, "commit", "-m", "Modify on branch");

        // Modify the same file differently on develop
        RunGit(_repoRoot, "checkout", "develop");
        File.WriteAllText(Path.Combine(_repoRoot, "rebase-conflict.txt"), "develop change");
        RunGit(_repoRoot, "add", "rebase-conflict.txt");
        RunGit(_repoRoot, "commit", "-m", "Modify on develop");

        var ex = await Assert.ThrowsAsync<MergeConflictException>(
            () => _service.RebaseAsync("task/rebase-conflict"));

        Assert.Equal("task/rebase-conflict", ex.Branch);
        Assert.Contains("rebase-conflict.txt", ex.ConflictingFiles);
    }

    // ── Helpers ─────────────────────────────────────────────────

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

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
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
