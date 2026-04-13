using System.Diagnostics;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for LIST_WORKTREES and CLEANUP_WORKTREES command handlers.
/// Uses real git repos + in-memory SQLite for full integration coverage.
/// </summary>
public sealed class WorktreeCommandHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _repoRoot;
    private readonly WorktreeService _worktreeService;
    private readonly List<string> _tempDirs = [];

    public WorktreeCommandHandlerTests()
    {
        _repoRoot = CreateTempDir("wt-cmd-test");
        InitializeRepository(_repoRoot);
        _worktreeService = new WorktreeService(
            NullLogger<WorktreeService>.Instance, _repoRoot, "git");

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton(_worktreeService);
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

    private CommandContext MakeContext(string role = "Planner")
    {
        var scope = _serviceProvider.CreateScope();
        return new CommandContext(
            AgentId: "test-agent",
            AgentName: "TestAgent",
            AgentRole: role,
            RoomId: "main",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider);
    }

    private static CommandEnvelope MakeEnvelope(string command, Dictionary<string, object?>? args = null)
        => new(
            Command: command,
            Args: args ?? new Dictionary<string, object?>(),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "test-agent");

    // ── LIST_WORKTREES ──────────────────────────────────────────

    [Fact]
    public async Task ListWorktrees_NoWorktrees_ReturnsEmptyList()
    {
        var handler = new ListWorktreesHandler();
        var result = await handler.ExecuteAsync(MakeEnvelope("LIST_WORKTREES"), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, data["count"]);
    }

    [Fact]
    public async Task ListWorktrees_WithWorktree_ReturnsBranchAndPath()
    {
        var branch = CreateFeatureBranch("list-test", "file.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);

        var handler = new ListWorktreesHandler();
        var result = await handler.ExecuteAsync(MakeEnvelope("LIST_WORKTREES"), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, data["count"]);
        var worktrees = Assert.IsAssignableFrom<IList<Dictionary<string, object?>>>(data["worktrees"]);
        Assert.Equal(branch, worktrees[0]["branch"]);
        Assert.NotNull(worktrees[0]["relativePath"]);
    }

    [Fact]
    public async Task ListWorktrees_WithLinkedTask_EnrichesWithTaskInfo()
    {
        var branch = CreateFeatureBranch("enrich-test", "enrich.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-enrich",
                Title = "Enrichment feature",
                Status = "Active",
                BranchName = branch,
                AssignedAgentId = "coder-1",
                AssignedAgentName = "Coder",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var handler = new ListWorktreesHandler();
        var result = await handler.ExecuteAsync(MakeEnvelope("LIST_WORKTREES"), MakeContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        var worktrees = Assert.IsAssignableFrom<IList<Dictionary<string, object?>>>(data["worktrees"]);
        Assert.Equal("task-enrich", worktrees[0]["taskId"]);
        Assert.Equal("Enrichment feature", worktrees[0]["taskTitle"]);
        Assert.Equal("Coder", worktrees[0]["agentName"]);
    }

    [Fact]
    public async Task ListWorktrees_StatusFilter_FiltersResults()
    {
        var branchActive = CreateFeatureBranch("filter-active", "active.txt", "a");
        var branchDone = CreateFeatureBranch("filter-done", "done.txt", "d");
        await _worktreeService.CreateWorktreeAsync(branchActive);
        await _worktreeService.CreateWorktreeAsync(branchDone);

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-active", Title = "Active work", Status = "Active",
                BranchName = branchActive, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-done", Title = "Done work", Status = "Completed",
                BranchName = branchDone, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var handler = new ListWorktreesHandler();
        var args = new Dictionary<string, object?> { ["status"] = "Completed" };
        var result = await handler.ExecuteAsync(MakeEnvelope("LIST_WORKTREES", args), MakeContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, data["count"]);
        var worktrees = Assert.IsAssignableFrom<IList<Dictionary<string, object?>>>(data["worktrees"]);
        Assert.Equal(branchDone, worktrees[0]["branch"]);
    }

    [Fact]
    public void ListWorktrees_CommandName_IsCorrect()
    {
        var handler = new ListWorktreesHandler();
        Assert.Equal("LIST_WORKTREES", handler.CommandName);
        Assert.True(handler.IsRetrySafe);
    }

    [Fact]
    public async Task ListWorktrees_MultipleWorktrees_ReturnsAll()
    {
        var branch1 = CreateFeatureBranch("multi-a", "a.txt", "a");
        var branch2 = CreateFeatureBranch("multi-b", "b.txt", "b");
        await _worktreeService.CreateWorktreeAsync(branch1);
        await _worktreeService.CreateWorktreeAsync(branch2);

        var handler = new ListWorktreesHandler();
        var result = await handler.ExecuteAsync(MakeEnvelope("LIST_WORKTREES"), MakeContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(2, data["count"]);
    }

    [Fact]
    public async Task ListWorktrees_AnyRole_CanExecute()
    {
        var branch = CreateFeatureBranch("role-test", "role.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);

        var handler = new ListWorktreesHandler();
        var result = await handler.ExecuteAsync(
            MakeEnvelope("LIST_WORKTREES"), MakeContext("SoftwareEngineer"));

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, data["count"]);
    }

    // ── CLEANUP_WORKTREES ───────────────────────────────────────

    [Fact]
    public async Task CleanupWorktrees_NoWorktrees_ReturnsZero()
    {
        var handler = new CleanupWorktreesHandler();
        var args = new Dictionary<string, object?> { ["confirm"] = true };
        var result = await handler.ExecuteAsync(MakeEnvelope("CLEANUP_WORKTREES", args), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, data["removedCount"]);
    }

    [Fact]
    public async Task CleanupWorktrees_RemovesCompletedTaskWorktrees()
    {
        var branch = CreateFeatureBranch("cleanup-done", "done.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-cleanup", Title = "Done task", Status = "Completed",
                BranchName = branch, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var handler = new CleanupWorktreesHandler();
        var args = new Dictionary<string, object?> { ["confirm"] = true };
        var result = await handler.ExecuteAsync(MakeEnvelope("CLEANUP_WORKTREES", args), MakeContext());

        Assert.Equal(CommandStatus.Success, result.Status);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, data["removedCount"]);
        var removed = Assert.IsAssignableFrom<IList<string>>(data["removedBranches"]);
        Assert.Contains(branch, removed);

        // Verify worktree was actually removed
        Assert.Empty(_worktreeService.GetActiveWorktrees());
    }

    [Fact]
    public async Task CleanupWorktrees_RemovesCancelledTaskWorktrees()
    {
        var branch = CreateFeatureBranch("cleanup-cancel", "cancel.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-cancel", Title = "Cancelled task", Status = "Cancelled",
                BranchName = branch, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var handler = new CleanupWorktreesHandler();
        var args = new Dictionary<string, object?> { ["confirm"] = true };
        var result = await handler.ExecuteAsync(MakeEnvelope("CLEANUP_WORKTREES", args), MakeContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, data["removedCount"]);
    }

    [Fact]
    public async Task CleanupWorktrees_LeavesActiveTaskWorktrees()
    {
        var branchActive = CreateFeatureBranch("keep-active", "keep.txt", "content");
        var branchDone = CreateFeatureBranch("remove-done", "remove.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branchActive);
        await _worktreeService.CreateWorktreeAsync(branchDone);

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-keep", Title = "Active work", Status = "Active",
                BranchName = branchActive, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            db.Tasks.Add(new TaskEntity
            {
                Id = "task-remove", Title = "Done work", Status = "Completed",
                BranchName = branchDone, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var handler = new CleanupWorktreesHandler();
        var args = new Dictionary<string, object?> { ["confirm"] = true };
        var result = await handler.ExecuteAsync(MakeEnvelope("CLEANUP_WORKTREES", args), MakeContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, data["removedCount"]);

        // Active worktree should remain
        var remaining = _worktreeService.GetActiveWorktrees();
        Assert.Single(remaining);
        Assert.Equal(branchActive, remaining[0].Branch);
    }

    [Fact]
    public async Task CleanupWorktrees_DeniedForNonPlanner()
    {
        var handler = new CleanupWorktreesHandler();
        var args = new Dictionary<string, object?> { ["confirm"] = true };
        var result = await handler.ExecuteAsync(
            MakeEnvelope("CLEANUP_WORKTREES", args), MakeContext("SoftwareEngineer"));

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    [Fact]
    public async Task CleanupWorktrees_AllowedForHuman()
    {
        var handler = new CleanupWorktreesHandler();
        var args = new Dictionary<string, object?> { ["confirm"] = true };
        var result = await handler.ExecuteAsync(
            MakeEnvelope("CLEANUP_WORKTREES", args), MakeContext("Human"));

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    [Fact]
    public async Task CleanupWorktrees_IncludeOrphans_RemovesUnlinkedWorktrees()
    {
        var branch = CreateFeatureBranch("orphan-test", "orphan.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);
        // No task linked to this branch

        var handler = new CleanupWorktreesHandler();
        var args = new Dictionary<string, object?>
        {
            ["confirm"] = true,
            ["includeOrphans"] = "true"
        };
        var result = await handler.ExecuteAsync(MakeEnvelope("CLEANUP_WORKTREES", args), MakeContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(1, data["removedCount"]);
    }

    [Fact]
    public async Task CleanupWorktrees_WithoutIncludeOrphans_LeavesUnlinkedWorktrees()
    {
        var branch = CreateFeatureBranch("no-orphan", "no-orphan.txt", "content");
        await _worktreeService.CreateWorktreeAsync(branch);
        // No task linked to this branch

        var handler = new CleanupWorktreesHandler();
        var args = new Dictionary<string, object?> { ["confirm"] = true };
        var result = await handler.ExecuteAsync(MakeEnvelope("CLEANUP_WORKTREES", args), MakeContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Result);
        Assert.Equal(0, data["removedCount"]);
        Assert.Single(_worktreeService.GetActiveWorktrees());
    }

    [Fact]
    public void CleanupWorktrees_IsDestructive()
    {
        var handler = new CleanupWorktreesHandler();
        Assert.True(handler.IsDestructive);
        Assert.Contains("worktrees", handler.DestructiveWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanupWorktrees_CommandName_IsCorrect()
    {
        var handler = new CleanupWorktreesHandler();
        Assert.Equal("CLEANUP_WORKTREES", handler.CommandName);
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
        RunGit(repoRoot, "config", "user.name", "Worktree Command Tests");
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
