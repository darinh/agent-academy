using System.Diagnostics;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="WorktreeController"/> — worktree status endpoint.
/// Uses real git repos + in-memory SQLite for full integration coverage.
/// </summary>
public sealed class WorktreeControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _repoRoot;
    private readonly WorktreeService _worktreeService;
    private readonly List<string> _tempDirs = [];

    public WorktreeControllerTests()
    {
        _repoRoot = CreateTempDir("wt-ctrl-test");
        InitializeRepository(_repoRoot);
        _worktreeService = new WorktreeService(
            NullLogger<WorktreeService>.Instance, _repoRoot, "git");

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _worktreeService.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
        foreach (var dir in _tempDirs)
        {
            try { ForceDeleteDirectory(dir); }
            catch { /* best-effort cleanup */ }
        }
    }

    private WorktreeController CreateController()
    {
        var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        return new WorktreeController(
            _worktreeService, db, NullLogger<WorktreeController>.Instance);
    }

    [Fact]
    public async Task GetAll_NoWorktrees_ReturnsEmptyList()
    {
        var controller = CreateController();

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WorktreeStatusSnapshot>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetAll_WithWorktree_ReturnsStatus()
    {
        var branch = CreateFeatureBranch("status-test", "test-file.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);
        var controller = CreateController();

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WorktreeStatusSnapshot>>(ok.Value);
        Assert.Single(list);
        var snapshot = list[0];
        Assert.Equal(branch, snapshot.Branch);
        Assert.True(snapshot.StatusAvailable);
        Assert.Equal(0, snapshot.TotalDirtyFiles);
        Assert.NotNull(snapshot.LastCommitSha);
    }

    [Fact]
    public async Task GetAll_WithLinkedTask_EnrichesWithTaskInfo()
    {
        var branch = CreateFeatureBranch("task-link", "link-file.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);

        // Seed a task with matching branch
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-42",
                Title = "Implement linked feature",
                Status = "Active",
                BranchName = branch,
                AssignedAgentId = "coder-1",
                AssignedAgentName = "Coder",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var controller = CreateController();
        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WorktreeStatusSnapshot>>(ok.Value);
        Assert.Single(list);
        var snapshot = list[0];
        Assert.Equal("task-42", snapshot.TaskId);
        Assert.Equal("Implement linked feature", snapshot.TaskTitle);
        Assert.Equal("Active", snapshot.TaskStatus);
        Assert.Equal("coder-1", snapshot.AgentId);
        Assert.Equal("Coder", snapshot.AgentName);
    }

    [Fact]
    public async Task GetAll_WithDirtyFiles_ReportsDirtyCount()
    {
        var branch = CreateFeatureBranch("dirty-test", "original.txt", "content");
        var wt = await _worktreeService.CreateWorktreeAsync(branch);

        // Dirty the worktree
        File.WriteAllText(Path.Combine(wt.Path, "new-file.txt"), "uncommitted");

        var controller = CreateController();
        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WorktreeStatusSnapshot>>(ok.Value);
        Assert.Single(list);
        Assert.Equal(1, list[0].TotalDirtyFiles);
        Assert.Contains("new-file.txt", list[0].DirtyFilesPreview);
    }

    [Fact]
    public async Task GetAll_MultipleWorktrees_ReturnsAll()
    {
        var branch1 = CreateFeatureBranch("multi-1", "file1.txt", "content1");
        var branch2 = CreateFeatureBranch("multi-2", "file2.txt", "content2");
        await _worktreeService.CreateWorktreeAsync(branch1);
        await _worktreeService.CreateWorktreeAsync(branch2);

        var controller = CreateController();
        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WorktreeStatusSnapshot>>(ok.Value);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, s => s.Branch == branch1);
        Assert.Contains(list, s => s.Branch == branch2);
    }

    [Fact]
    public async Task GetAll_NoMatchingTask_TaskFieldsAreNull()
    {
        var branch = CreateFeatureBranch("no-task", "orphan.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);
        var controller = CreateController();

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WorktreeStatusSnapshot>>(ok.Value);
        var snapshot = list[0];
        Assert.Null(snapshot.TaskId);
        Assert.Null(snapshot.TaskTitle);
        Assert.Null(snapshot.AgentId);
    }

    [Fact]
    public async Task GetAll_RelativePath_OmitsAbsolutePrefix()
    {
        var branch = CreateFeatureBranch("rel-path", "path-file.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);
        var controller = CreateController();

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WorktreeStatusSnapshot>>(ok.Value);
        // Relative path should NOT start with / or contain the temp dir root
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), list[0].RelativePath);
    }

    // ── Helpers ──────────────────────────────────────────────────

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
        RunGit(repoRoot, "config", "user.name", "Worktree Controller Tests");
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
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed");
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
