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
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for the REJECT_TASK command handler and WorkspaceRuntime.RejectTaskAsync.
/// </summary>
[Collection("WorkspaceRuntime")]
public class RejectTaskTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;
    private readonly GitService _gitService;
    private readonly string _repoRoot;

    public RejectTaskTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _repoRoot = Path.Combine(Path.GetTempPath(), $"agent-academy-reject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
        InitializeRepository(_repoRoot);
        // Place wrapper OUTSIDE repo root so git add -A doesn't track it
        var gitWrapper = CreateGitWrapper(Path.GetTempPath());

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false)
            ]
        );

        _gitService = new GitService(NullLogger<GitService>.Instance, _repoRoot, gitWrapper);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<MessageService>();
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(NSubstitute.Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
        services.AddSingleton(_gitService);
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
    }

    // ── Role Gate ───────────────────────────────────────────────

    [Fact]
    public async Task RejectTask_DeniedFor_Engineer()
    {
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = "t1", ["reason"] = "bugs" },
            agentId: "engineer-1", agentRole: "SoftwareEngineer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
    }

    [Theory]
    [InlineData("Planner")]
    [InlineData("Reviewer")]
    [InlineData("Human")]
    public async Task RejectTask_AllowedRoles_PassRoleGate(string role)
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Approved));
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(
            new() { ["taskId"] = taskId, ["reason"] = "issues found" },
            agentRole: role, agentId: role == "Human" ? "planner-1" : "reviewer-1");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── Validation ──────────────────────────────────────────────

    [Fact]
    public async Task RejectTask_MissingTaskId_ReturnsValidationError()
    {
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["reason"] = "bugs" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("taskId", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectTask_MissingReason_ReturnsValidationError()
    {
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = "task-1" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
        Assert.Contains("reason", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectTask_TaskNotFound_ReturnsNotFoundError()
    {
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = "nonexistent", ["reason"] = "bugs" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Status Gate ─────────────────────────────────────────────

    [Theory]
    [InlineData("Active")]
    [InlineData("InReview")]
    [InlineData("Merging")]
    [InlineData("ChangesRequested")]
    public async Task RejectTask_WrongStatus_ReturnsConflictError(string status)
    {
        var taskId = await CreateTestTask(status: status);
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId, ["reason"] = "issues" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Conflict, result.ErrorCode);
    }

    // ── Success: Approved → ChangesRequested ────────────────────

    [Fact]
    public async Task RejectTask_Approved_SetsChangesRequested()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Approved));
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId, ["reason"] = "Missing error handling" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("ChangesRequested", dict["status"]?.ToString());
        Assert.Equal("reviewer-1", dict["reviewerAgentId"]?.ToString());
    }

    [Fact]
    public async Task RejectTask_Approved_IncrementsReviewRounds()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Approved));
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId, ["reason"] = "edge cases" });

        await handler.ExecuteAsync(cmd, ctx);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Equal(1, task!.ReviewRounds);
    }

    [Fact]
    public async Task RejectTask_ErrorWhenMaxReviewRoundsExceeded()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Approved), reviewRounds: 5);
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId, ["reason"] = "more issues" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("maximum", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectTask_Approved_PostsRejectionMessage()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Approved));
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId, ["reason"] = "Missing tests" });

        await handler.ExecuteAsync(cmd, ctx);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var messages = await db.Messages.Where(m => m.RoomId == "room-1").ToListAsync();
        Assert.Contains(messages, m => m.Content.Contains("Rejected") && m.Content.Contains("Missing tests"));
    }

    // ── Success: Completed → ChangesRequested (with revert) ─────

    [Fact]
    public async Task RejectTask_Completed_SetsChangesRequested_ClearsMerge()
    {
        var branchName = $"task/reject-test-{Guid.NewGuid():N}"[..30];
        CreateFeatureBranchWithCommit(branchName, "feature.txt", "content");

        // Squash-merge to get a real merge commit
        var mergeCommitSha = await _gitService.SquashMergeAsync(branchName, "feat: test feature");

        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Completed),
            branchName: branchName,
            mergeCommitSha: mergeCommitSha);

        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId, ["reason"] = "Breaks production" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var dict = (Dictionary<string, object?>)result.Result!;
        Assert.Equal("ChangesRequested", dict["status"]?.ToString());
        Assert.NotNull(dict["revertCommitSha"]);

        // Verify DB state
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var task = await db.Tasks.FindAsync(taskId);
        Assert.Null(task!.MergeCommitSha);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public async Task RejectTask_Completed_RevertsOnDevelop()
    {
        var branchName = $"task/revert-test-{Guid.NewGuid():N}"[..30];
        CreateFeatureBranchWithCommit(branchName, "revert-me.txt", "should be reverted");

        var mergeCommitSha = await _gitService.SquashMergeAsync(branchName, "feat: to revert");
        Assert.True(File.Exists(Path.Combine(_repoRoot, "revert-me.txt")));

        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Completed),
            branchName: branchName,
            mergeCommitSha: mergeCommitSha);

        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId, ["reason"] = "Feature is wrong" });

        await handler.ExecuteAsync(cmd, ctx);

        // The revert commit should have undone the merge
        Assert.False(File.Exists(Path.Combine(_repoRoot, "revert-me.txt")));
    }

    // ── Breakout Room Reopening ─────────────────────────────────

    [Fact]
    public async Task RejectTask_ReopensArchivedBreakoutRoom()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Approved));
        await CreateArchivedBreakout(taskId, "engineer-1");

        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId, ["reason"] = "needs fixes" });

        await handler.ExecuteAsync(cmd, ctx);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var breakout = await db.BreakoutRooms.FirstOrDefaultAsync(b => b.TaskId == taskId);
        Assert.NotNull(breakout);
        Assert.Equal("Active", breakout.Status);
        Assert.Null(breakout.CloseReason);
    }

    [Fact]
    public async Task RejectTask_PostsFindingsToBreakoutRoom()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Approved));
        var breakoutId = await CreateArchivedBreakout(taskId, "engineer-1");

        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["taskId"] = taskId, ["reason"] = "Missing edge case handling" });

        await handler.ExecuteAsync(cmd, ctx);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var messages = await db.BreakoutMessages.Where(m => m.BreakoutRoomId == breakoutId).ToListAsync();
        Assert.Contains(messages, m => m.Content.Contains("Missing edge case handling"));
    }

    [Fact]
    public async Task RejectTask_TaskIdViaValue_Works()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Approved));
        var handler = new RejectTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand(new() { ["value"] = taskId, ["reason"] = "issues" });

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private async Task<string> CreateTestTask(
        string status = "Active",
        string? branchName = "task/test-abc123",
        string? mergeCommitSha = null,
        string title = "Test Task",
        int reviewRounds = 0)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await EnsureRoom(db, "room-1");

        var taskId = $"task-{Guid.NewGuid():N}"[..20];
        var entity = new TaskEntity
        {
            Id = taskId,
            Title = title,
            Description = "Test task description",
            SuccessCriteria = "It works",
            Status = status,
            Type = TaskType.Feature.ToString(),
            CurrentPhase = "Implementation",
            RoomId = "room-1",
            BranchName = branchName,
            AssignedAgentId = "engineer-1",
            ReviewRounds = reviewRounds,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MergeCommitSha = mergeCommitSha
        };

        if (status == nameof(TaskStatus.Completed))
            entity.CompletedAt = DateTime.UtcNow;

        db.Tasks.Add(entity);
        await db.SaveChangesAsync();
        return taskId;
    }

    private async Task<string> CreateArchivedBreakout(string taskId, string agentId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await EnsureRoom(db, "room-1");

        var breakoutId = Guid.NewGuid().ToString("N");
        db.BreakoutRooms.Add(new BreakoutRoomEntity
        {
            Id = breakoutId,
            Name = "Fix: test task",
            ParentRoomId = "room-1",
            AssignedAgentId = agentId,
            Status = "Archived",
            CloseReason = "Completed",
            TaskId = taskId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return breakoutId;
    }

    private static async Task EnsureRoom(AgentAcademyDbContext db, string roomId)
    {
        if (await db.Rooms.FindAsync(roomId) is not null) return;
        db.Rooms.Add(new RoomEntity
        {
            Id = roomId,
            Name = "Test Room",
            Status = "Active",
            CurrentPhase = "Implementation",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static void InitializeRepository(string repoRoot)
    {
        RunGit(repoRoot, "init");
        RunGit(repoRoot, "config", "user.name", "Agent Academy Tests");
        RunGit(repoRoot, "config", "user.email", "tests@agent-academy.local");
        RunGit(repoRoot, "checkout", "-b", "develop");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "initial\n");
        RunGit(repoRoot, "add", "README.md");
        RunGit(repoRoot, "commit", "-m", "Initial commit");
    }

    private static string CreateGitWrapper(string directory)
    {
        var wrapperPath = Path.Combine(directory, $"git-wrapper-{Guid.NewGuid():N}.sh");
        File.WriteAllText(wrapperPath,
            """
            #!/usr/bin/env bash
            set -euo pipefail
            exec git "$@"
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                wrapperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return wrapperPath;
    }

    private void CreateFeatureBranchWithCommit(string branchName, string fileName, string content)
    {
        RunGitInRepo("checkout", "-b", branchName);
        File.WriteAllText(Path.Combine(_repoRoot, fileName), content + Environment.NewLine);
        RunGitInRepo("add", fileName);
        RunGitInRepo("commit", "-m", $"Add {fileName}");
        RunGitInRepo("checkout", "develop");
    }

    private string RunGitInRepo(params string[] args) => RunGit(_repoRoot, args);

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

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git for test");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr.Trim()}");
        }
        return stdout.Trim();
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        Dictionary<string, string> args,
        string agentId = "reviewer-1",
        string agentName = "Socrates",
        string agentRole = "Reviewer")
    {
        var scope = _serviceProvider.CreateScope();

        var command = new CommandEnvelope(
            Command: "REJECT_TASK",
            Args: args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: agentId
        );

        var context = new CommandContext(
            AgentId: agentId,
            AgentName: agentName,
            AgentRole: agentRole,
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider
        );

        return (command, context);
    }
}
