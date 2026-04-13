using System.Diagnostics;
using AgentAcademy.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="WorktreeService"/> — git worktree lifecycle management.
/// Each test creates a temporary git repository to isolate from the real project.
/// </summary>
public class WorktreeServiceTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly WorktreeService _service;
    private readonly List<string> _tempDirs = [];

    public WorktreeServiceTests()
    {
        _repoRoot = CreateTempDir("worktree-test-repo");
        InitializeRepository(_repoRoot);
        _service = new WorktreeService(
            NullLogger<WorktreeService>.Instance, _repoRoot, "git");
    }

    public void Dispose()
    {
        _service.Dispose();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in _tempDirs)
        {
            try
            {
                // Safety: never delete home-level directories like ~/projects/
                if (string.Equals(dir, home, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dir, Path.Combine(home, "projects"), StringComparison.OrdinalIgnoreCase) ||
                    dir.Split(Path.DirectorySeparatorChar).Length <= 3)
                    continue;

                // git worktree directories have read-only .git files — force-delete
                ForceDeleteDirectory(dir);
            }
            catch { /* best effort cleanup */ }
        }
    }

    // ── CreateWorktreeAsync ─────────────────────────────────────

    [Fact]
    public async Task CreateWorktreeAsync_CreatesDirectoryAndReturnsInfo()
    {
        var branch = CreateFeatureBranch("feature-one", "file1.txt", "content");

        var info = await _service.CreateWorktreeAsync(branch);

        Assert.Equal(branch, info.Branch);
        Assert.True(Directory.Exists(info.Path), "Worktree directory should exist");
        Assert.True(File.Exists(Path.Combine(info.Path, "file1.txt")),
            "Worktree should contain the branch's file");
    }

    [Fact]
    public async Task CreateWorktreeAsync_ThrowsForNullBranch()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateWorktreeAsync(null!));
    }

    [Fact]
    public async Task CreateWorktreeAsync_ThrowsForEmptyBranch()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateWorktreeAsync(""));
    }

    [Fact]
    public async Task CreateWorktreeAsync_ThrowsForNonexistentBranch()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateWorktreeAsync("no-such-branch"));
    }

    [Fact]
    public async Task CreateWorktreeAsync_ReturnsSameInfoOnSecondCall()
    {
        var branch = CreateFeatureBranch("feature-idempotent", "f.txt", "data");

        var first = await _service.CreateWorktreeAsync(branch);
        var second = await _service.CreateWorktreeAsync(branch);

        Assert.Equal(first.Path, second.Path);
        Assert.Equal(first.Branch, second.Branch);
    }

    [Fact]
    public async Task CreateWorktreeAsync_RecreatesIfDirectoryDeleted()
    {
        var branch = CreateFeatureBranch("feature-recreate", "r.txt", "data");

        var first = await _service.CreateWorktreeAsync(branch);
        var firstPath = first.Path;

        // Simulate stale directory removal
        await _service.RemoveWorktreeAsync(branch);
        Assert.False(Directory.Exists(firstPath));

        var second = await _service.CreateWorktreeAsync(branch);
        Assert.True(Directory.Exists(second.Path));
    }

    [Fact]
    public async Task CreateWorktreeAsync_MultiplebranchesInParallel()
    {
        var branches = Enumerable.Range(1, 3)
            .Select(i => CreateFeatureBranch($"parallel-{i}", $"p{i}.txt", $"content-{i}"))
            .ToList();

        var tasks = branches.Select(b => _service.CreateWorktreeAsync(b));
        var results = await Task.WhenAll(tasks);

        Assert.Equal(3, results.Length);
        Assert.All(results, info => Assert.True(Directory.Exists(info.Path)));
        Assert.Equal(3, results.Select(r => r.Path).Distinct().Count());
    }

    [Fact]
    public async Task CreateWorktreeAsync_SimilarBranchNamesGetDistinctPaths()
    {
        // These two branches produce the same safe prefix after regex replacement
        // but the hash suffix ensures distinct worktree paths
        var b1 = CreateFeatureBranch("x_y", "xy1.txt", "data1");   // task/x_y → safe: task_x_y
        var b2 = CreateFeatureBranch("x.y", "xy2.txt", "data2");   // task/x.y → safe: task_x_y (same!)

        var info1 = await _service.CreateWorktreeAsync(b1);
        var info2 = await _service.CreateWorktreeAsync(b2);

        Assert.NotEqual(info1.Path, info2.Path);
        Assert.True(Directory.Exists(info1.Path));
        Assert.True(Directory.Exists(info2.Path));
    }

    // ── RemoveWorktreeAsync ─────────────────────────────────────

    [Fact]
    public async Task RemoveWorktreeAsync_RemovesDirectoryAndTracking()
    {
        var branch = CreateFeatureBranch("feature-remove", "rm.txt", "data");
        var info = await _service.CreateWorktreeAsync(branch);
        Assert.True(Directory.Exists(info.Path));

        await _service.RemoveWorktreeAsync(branch);

        Assert.False(Directory.Exists(info.Path));
        Assert.Null(_service.GetWorktreePath(branch));
    }

    [Fact]
    public async Task RemoveWorktreeAsync_SafeForNonexistentBranch()
    {
        // Should not throw
        await _service.RemoveWorktreeAsync("no-such-branch");
    }

    [Fact]
    public async Task RemoveWorktreeAsync_SafeForNullBranch()
    {
        // Should not throw
        await _service.RemoveWorktreeAsync(null!);
    }

    [Fact]
    public async Task RemoveWorktreeAsync_IdempotentOnDoubleRemove()
    {
        var branch = CreateFeatureBranch("feature-double-rm", "d.txt", "data");
        await _service.CreateWorktreeAsync(branch);

        await _service.RemoveWorktreeAsync(branch);
        await _service.RemoveWorktreeAsync(branch); // second call should not throw
    }

    // ── GetWorktreePath ─────────────────────────────────────────

    [Fact]
    public async Task GetWorktreePath_ReturnsPathForActiveWorktree()
    {
        var branch = CreateFeatureBranch("feature-path", "g.txt", "data");
        var info = await _service.CreateWorktreeAsync(branch);

        Assert.Equal(info.Path, _service.GetWorktreePath(branch));
    }

    [Fact]
    public void GetWorktreePath_ReturnsNullForUnknownBranch()
    {
        Assert.Null(_service.GetWorktreePath("unknown-branch"));
    }

    // ── GetActiveWorktrees ──────────────────────────────────────

    [Fact]
    public async Task GetActiveWorktrees_ReturnsAllTracked()
    {
        var b1 = CreateFeatureBranch("active-1", "a1.txt", "data");
        var b2 = CreateFeatureBranch("active-2", "a2.txt", "data");

        await _service.CreateWorktreeAsync(b1);
        await _service.CreateWorktreeAsync(b2);

        var active = _service.GetActiveWorktrees();
        Assert.Equal(2, active.Count);
        Assert.Contains(active, w => w.Branch == b1);
        Assert.Contains(active, w => w.Branch == b2);
    }

    // ── ListGitWorktreesAsync ───────────────────────────────────

    [Fact]
    public async Task ListGitWorktreesAsync_IncludesMainAndWorktree()
    {
        var branch = CreateFeatureBranch("listed-wt", "l.txt", "data");
        await _service.CreateWorktreeAsync(branch);

        var entries = await _service.ListGitWorktreesAsync();

        // At minimum: main repo + our worktree
        Assert.True(entries.Count >= 2);
        Assert.Contains(entries, e => e.Branch == branch);
    }

    // ── CleanupAllWorktreesAsync ────────────────────────────────

    [Fact]
    public async Task CleanupAllWorktreesAsync_RemovesEverything()
    {
        var b1 = CreateFeatureBranch("cleanup-1", "c1.txt", "data");
        var b2 = CreateFeatureBranch("cleanup-2", "c2.txt", "data");
        await _service.CreateWorktreeAsync(b1);
        await _service.CreateWorktreeAsync(b2);

        await _service.CleanupAllWorktreesAsync();

        Assert.Empty(_service.GetActiveWorktrees());
        Assert.Null(_service.GetWorktreePath(b1));
        Assert.Null(_service.GetWorktreePath(b2));
    }

    // ── SyncWithGitAsync ────────────────────────────────────────

    [Fact]
    public async Task SyncWithGitAsync_RemovesStalePaths()
    {
        var branch = CreateFeatureBranch("sync-stale", "s.txt", "data");
        var info = await _service.CreateWorktreeAsync(branch);

        // Forcibly remove directory to simulate crash
        RunGit(_repoRoot, "worktree", "remove", "--force", info.Path);

        await _service.SyncWithGitAsync();

        Assert.Null(_service.GetWorktreePath(branch));
    }

    // ── Worktree Isolation ──────────────────────────────────────

    [Fact]
    public async Task Worktree_FileChangesAreIsolated()
    {
        var branch = CreateFeatureBranch("isolated-wt", "shared.txt", "original");
        var info = await _service.CreateWorktreeAsync(branch);

        // Modify file in worktree (uncommitted)
        File.WriteAllText(Path.Combine(info.Path, "shared.txt"), "modified-in-worktree");

        // Main repo (on develop) should not have the branch file at all
        Assert.False(File.Exists(Path.Combine(_repoRoot, "shared.txt")),
            "Develop branch should not have the feature file");

        // Worktree has the uncommitted modification
        var wtContent = File.ReadAllText(Path.Combine(info.Path, "shared.txt"));
        Assert.Equal("modified-in-worktree", wtContent);

        // The branch's committed content is accessible via git show
        var committedContent = RunGit(_repoRoot, "show", $"{branch}:shared.txt");
        Assert.Equal("original", committedContent.TrimEnd());
    }

    [Fact]
    public async Task Worktree_CommitsAreOnCorrectBranch()
    {
        var branch = CreateFeatureBranch("commit-wt", "commit.txt", "v1");
        var info = await _service.CreateWorktreeAsync(branch);

        // Make a commit in the worktree
        File.WriteAllText(Path.Combine(info.Path, "commit.txt"), "v2");
        RunGit(info.Path, "add", "commit.txt");
        RunGit(info.Path, "commit", "-m", "Update in worktree");

        // Verify the commit is on the worktree's branch
        var wtLog = RunGit(info.Path, "log", "--oneline", "-1");
        Assert.Contains("Update in worktree", wtLog);

        // Main repo develop should not have the commit
        var developLog = RunGit(_repoRoot, "log", "--oneline", "-1");
        Assert.DoesNotContain("Update in worktree", developLog);
    }

    // ── ParseWorktreeList (static, no git needed) ───────────────

    [Fact]
    public void ParseWorktreeList_ParsesPorcelainOutput()
    {
        var porcelain = """
            worktree /repo/root
            HEAD abc123
            branch refs/heads/develop

            worktree /repo/.worktrees/task_feature-a1b2c3
            HEAD def456
            branch refs/heads/task/feature-a1b2c3

            """;

        var entries = WorktreeService.ParseWorktreeList(porcelain);

        Assert.Equal(2, entries.Count);
        Assert.Equal("/repo/root", entries[0].Path);
        Assert.Equal("develop", entries[0].Branch);
        Assert.Equal("/repo/.worktrees/task_feature-a1b2c3", entries[1].Path);
        Assert.Equal("task/feature-a1b2c3", entries[1].Branch);
    }

    [Fact]
    public void ParseWorktreeList_HandlesBareRepo()
    {
        var porcelain = """
            worktree /repo/bare
            HEAD abc123
            bare

            """;

        var entries = WorktreeService.ParseWorktreeList(porcelain);

        Assert.Single(entries);
        Assert.True(entries[0].Bare);
        Assert.Null(entries[0].Branch);
    }

    [Fact]
    public void ParseWorktreeList_HandlesEmptyOutput()
    {
        var entries = WorktreeService.ParseWorktreeList("");
        Assert.Empty(entries);
    }

    // ── BuildAgentWorktreePath (static) ─────────────────────────

    [Fact]
    public void BuildAgentWorktreePath_ProducesExpectedLayout()
    {
        var path = WorktreeService.BuildAgentWorktreePath("Agent Academy", "software-engineer-1");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Without workspacePath, uses simple format
        var expected = Path.Combine(home, "projects", "agent-academy-worktrees", "software-engineer-1");
        Assert.Equal(expected, path);
    }

    [Fact]
    public void BuildAgentWorktreePath_IncludesWorkspaceHashWhenProvided()
    {
        var path1 = WorktreeService.BuildAgentWorktreePath("My App", "agent-1", "/path/to/workspace-a");
        var path2 = WorktreeService.BuildAgentWorktreePath("My App", "agent-1", "/path/to/workspace-b");
        // Different workspaces with same project name must produce different paths
        Assert.NotEqual(path1, path2);
        Assert.Contains("my-app-worktrees-", path1);
    }

    [Fact]
    public void BuildAgentWorktreePath_SanitizesSpecialCharacters()
    {
        var path = WorktreeService.BuildAgentWorktreePath("My Project!@#", "agent/test.1");
        Assert.DoesNotContain("!", path);
        Assert.DoesNotContain("@", path);
        Assert.Contains("my-project", path);
        Assert.Contains("agent-test-1", path);
    }

    // ── EnsureAgentWorktreeAsync ────────────────────────────────

    [Fact]
    public async Task EnsureAgentWorktreeAsync_ThrowsForNullWorkspace()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.EnsureAgentWorktreeAsync(null!, "proj", "agent-1", "develop"));
    }

    [Fact]
    public async Task EnsureAgentWorktreeAsync_ThrowsForNullAgentId()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.EnsureAgentWorktreeAsync("/workspace", "proj", null!, "develop"));
    }

    [Fact]
    public async Task EnsureAgentWorktreeAsync_ThrowsForNullBranch()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.EnsureAgentWorktreeAsync("/workspace", "proj", "agent-1", null!));
    }

    [Fact]
    public async Task EnsureAgentWorktreeAsync_CreatesWorktreeAndReturnsPath()
    {
        var branch = CreateFeatureBranch("agent-wt-test", "agent.txt", "content");
        var path = await _service.EnsureAgentWorktreeAsync(_repoRoot, "test-project", "agent-1", branch);
        Assert.True(Directory.Exists(path), "Agent worktree directory should exist");
        Assert.True(File.Exists(Path.Combine(path, "agent.txt")), "Worktree should contain the branch's file");
        _tempDirs.Add(path);
        var parentDir = Path.GetDirectoryName(path);
        if (parentDir is not null) _tempDirs.Add(parentDir);
    }

    [Fact]
    public async Task EnsureAgentWorktreeAsync_IsIdempotent()
    {
        var branch = CreateFeatureBranch("agent-wt-idem", "f.txt", "data");
        var path1 = await _service.EnsureAgentWorktreeAsync(_repoRoot, "test-project", "agent-2", branch);
        var path2 = await _service.EnsureAgentWorktreeAsync(_repoRoot, "test-project", "agent-2", branch);
        Assert.Equal(path1, path2);
        _tempDirs.Add(path1);
        var parentDir = Path.GetDirectoryName(path1);
        if (parentDir is not null) _tempDirs.Add(parentDir);
    }

    [Fact]
    public async Task RemoveAgentWorktreeAsync_CleansUp()
    {
        var branch = CreateFeatureBranch("agent-wt-rm", "r.txt", "data");
        var path = await _service.EnsureAgentWorktreeAsync(_repoRoot, "test-project", "agent-3", branch);
        Assert.True(Directory.Exists(path));
        await _service.RemoveAgentWorktreeAsync(_repoRoot, "test-project", "agent-3");
        Assert.False(Directory.Exists(path));
        var parentDir = Path.GetDirectoryName(path);
        if (parentDir is not null) _tempDirs.Add(parentDir);
    }

    [Fact]
    public void GetAgentWorktreePath_ReturnsNullWhenNoneExists()
    {
        var result = _service.GetAgentWorktreePath("nonexistent", "agent-99");
        Assert.Null(result);
    }

    // ── GetWorktreeGitStatusAsync Tests ──────────────────────────

    [Fact]
    public async Task GetWorktreeGitStatus_CleanWorktree_ReportsZeroDirtyFiles()
    {
        var branch = CreateFeatureBranch("clean-status", "status-file.txt", "content");
        var wt = await _service.CreateWorktreeAsync(branch);

        var status = await _service.GetWorktreeGitStatusAsync(wt.Path);

        Assert.True(status.StatusAvailable);
        Assert.Null(status.Error);
        Assert.Equal(0, status.TotalDirtyFiles);
        Assert.Empty(status.DirtyFilesPreview);
    }

    [Fact]
    public async Task GetWorktreeGitStatus_DirtyWorktree_ReportsDirtyFiles()
    {
        var branch = CreateFeatureBranch("dirty-status", "clean-file.txt", "content");
        var wt = await _service.CreateWorktreeAsync(branch);

        // Create dirty files in the worktree
        File.WriteAllText(Path.Combine(wt.Path, "dirty1.txt"), "modified");
        File.WriteAllText(Path.Combine(wt.Path, "dirty2.txt"), "new file");

        var status = await _service.GetWorktreeGitStatusAsync(wt.Path);

        Assert.True(status.StatusAvailable);
        Assert.Equal(2, status.TotalDirtyFiles);
        Assert.Equal(2, status.DirtyFilesPreview.Count);
        Assert.Contains("dirty1.txt", status.DirtyFilesPreview);
        Assert.Contains("dirty2.txt", status.DirtyFilesPreview);
    }

    [Fact]
    public async Task GetWorktreeGitStatus_ReturnsLastCommitInfo()
    {
        var branch = CreateFeatureBranch("commit-info", "info-file.txt", "content");
        var wt = await _service.CreateWorktreeAsync(branch);

        var status = await _service.GetWorktreeGitStatusAsync(wt.Path);

        Assert.True(status.StatusAvailable);
        Assert.NotNull(status.LastCommitSha);
        Assert.Equal(40, status.LastCommitSha!.Length);
        Assert.NotNull(status.LastCommitMessage);
        Assert.Contains("info-file.txt", status.LastCommitMessage!);
        Assert.NotNull(status.LastCommitAuthor);
        Assert.NotNull(status.LastCommitDate);
    }

    [Fact]
    public async Task GetWorktreeGitStatus_ReportsDiffStats()
    {
        var branch = CreateFeatureBranch("diff-stats", "base-file.txt", "line1\nline2\nline3\n");
        var wt = await _service.CreateWorktreeAsync(branch);

        // Modify the tracked file to generate diff stats
        File.WriteAllText(Path.Combine(wt.Path, "base-file.txt"), "line1\nchanged\nline3\nnew line\n");

        var status = await _service.GetWorktreeGitStatusAsync(wt.Path);

        Assert.True(status.StatusAvailable);
        // Should have some insertions/deletions
        Assert.True(status.FilesChanged > 0 || status.Insertions > 0 || status.Deletions > 0);
    }

    [Fact]
    public async Task GetWorktreeGitStatus_NonexistentPath_ReturnsUnavailable()
    {
        var status = await _service.GetWorktreeGitStatusAsync("/nonexistent/path/worktree");

        Assert.False(status.StatusAvailable);
        Assert.NotNull(status.Error);
        Assert.Contains("does not exist", status.Error!);
    }

    [Fact]
    public async Task GetWorktreeGitStatus_DirtyFilesCapped_PreviewLimited()
    {
        var branch = CreateFeatureBranch("cap-test", "initial.txt", "content");
        var wt = await _service.CreateWorktreeAsync(branch);

        // Create more than 10 dirty files
        for (int i = 0; i < 15; i++)
            File.WriteAllText(Path.Combine(wt.Path, $"file-{i:D2}.txt"), $"content-{i}");

        var status = await _service.GetWorktreeGitStatusAsync(wt.Path);

        Assert.True(status.StatusAvailable);
        Assert.Equal(15, status.TotalDirtyFiles);
        Assert.Equal(10, status.DirtyFilesPreview.Count);
    }

    [Fact]
    public void RepositoryRoot_ExposesConfiguredRoot()
    {
        Assert.Equal(_repoRoot, _service.RepositoryRoot);
    }

    // ── Test Helpers ────────────────────────────────────────────

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
        RunGit(repoRoot, "config", "user.name", "Worktree Tests");
        RunGit(repoRoot, "config", "user.email", "tests@worktree.local");
        RunGit(repoRoot, "checkout", "-b", "develop");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "initial\n");
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

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git for test");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed: {stderr.Trim()}");

        return stdout.Trim();
    }

    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;

        // Clear read-only attributes that git sets on .git files
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}
