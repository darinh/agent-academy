using System.Diagnostics;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for MERGE_TASK command handler and branch workflow authorization.
/// </summary>
[Collection("WorkspaceRuntime")]
public class BranchWorkflowTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;
    private readonly GitService _gitService;
    private readonly string _gitTracePath;
    private readonly string _repoRoot;

    public BranchWorkflowTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _repoRoot = Path.Combine(Path.GetTempPath(), $"agent-academy-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
        InitializeRepository(_repoRoot);
        _gitTracePath = Path.Combine(_repoRoot, "git-trace.log");
        var gitWrapper = CreateGitWrapper(_repoRoot, _gitTracePath);

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["MERGE_TASK", "APPROVE_TASK"], [])),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["LIST_*"], ["MERGE_TASK"])),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false,
                    Permissions: new CommandPermissionSet(["MERGE_TASK", "APPROVE_TASK"], []))
            ]
        );

        _gitService = new GitService(NullLogger<GitService>.Instance, _repoRoot, gitWrapper);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton(_catalog);
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(NSubstitute.Substitute.For<IAgentExecutor>());
        services.AddScoped<ConversationSessionService>();
        services.AddSingleton(_gitService);
        services.AddSingleton<CommandAuthorizer>();
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

    // ── Authorization Tests ─────────────────────────────────────

    [Fact]
    public async Task MergeTask_Reviewer_CanMerge_WhenApproved()
    {
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: "task/test-branch-abc123");
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var authorizer = _serviceProvider.GetRequiredService<CommandAuthorizer>();
        var agent = _catalog.Agents.First(a => a.Id == "reviewer-1");
        var denied = authorizer.Authorize(cmd, agent);

        Assert.Null(denied); // NOT Denied
    }

    [Fact]
    public async Task MergeTask_Planner_CanMerge()
    {
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: "task/test-branch-abc123");
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "planner-1", "Aristotle", "Planner");

        var authorizer = _serviceProvider.GetRequiredService<CommandAuthorizer>();
        var agent = _catalog.Agents.First(a => a.Id == "planner-1");
        var denied = authorizer.Authorize(cmd, agent);

        Assert.Null(denied); // NOT Denied
    }

    [Fact]
    public async Task MergeTask_Engineer_Denied()
    {
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: "task/test-branch-abc123");
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus", "SoftwareEngineer");

        var authorizer = _serviceProvider.GetRequiredService<CommandAuthorizer>();
        var agent = _catalog.Agents.First(a => a.Id == "engineer-1");
        var denied = authorizer.Authorize(cmd, agent);

        Assert.NotNull(denied);
        Assert.Equal(CommandStatus.Denied, denied!.Status);
    }

    [Fact]
    public async Task MergeTask_ExecuteAsync_EngineerRole_ReturnsDenied()
    {
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: "task/test-branch-abc123");
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "engineer-1", "Hephaestus", "SoftwareEngineer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Equal("Only Planner or Reviewer roles can merge tasks", result.Error);
    }

    // ── Validation Tests ────────────────────────────────────────

    [Fact]
    public async Task MergeTask_NotApproved_Blocked()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Active));
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Approved", result.Error!);
    }

    [Fact]
    public async Task MergeTask_NoBranch_Blocked()
    {
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: null);
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("branch", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MergeTask_MissingTaskId_ReturnsError()
    {
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new(), "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("TaskId", result.Error!);
    }

    [Fact]
    public async Task MergeTask_NonexistentTask_ReturnsError()
    {
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = "nonexistent-task-id" }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("not found", result.Error!);
    }

    [Fact]
    public async Task MergeTask_InReview_Blocked()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.InReview));
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Approved", result.Error!);
    }

    [Fact]
    public async Task MergeTask_Success_ReturnsAndPersistsMergeCommitSha()
    {
        const string branchName = "task/test-branch-abc123";
        CreateFeatureBranchWithCommit(branchName, "feature.txt", "branch workflow integration fix");

        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: branchName);
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var mergeCommitSha = result.Result!["mergeCommitSha"]?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(mergeCommitSha));
        Assert.Equal(mergeCommitSha, RunGitInRepo("rev-parse", "HEAD"));

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var task = await runtime.GetTaskAsync(taskId);

        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Completed, task!.Status);
        Assert.Equal(mergeCommitSha, task.MergeCommitSha);
    }

    [Theory]
    [InlineData(TaskType.Feature, "feat: ", "feature")]
    [InlineData(TaskType.Bug, "fix: ", "bug")]
    [InlineData(TaskType.Chore, "chore: ", "chore")]
    [InlineData(TaskType.Spike, "docs: ", "spike")]
    public async Task MergeTask_UsesConventionalCommitPrefix(TaskType taskType, string expectedPrefix, string branchSuffix)
    {
        var title = $"Conventional commit {branchSuffix}";
        var branchName = $"task/{branchSuffix}-merge-prefix";
        CreateFeatureBranchWithCommit(branchName, $"{branchSuffix}.txt", $"content for {branchSuffix}");

        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: branchName,
            title: title,
            taskType: taskType);
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal($"{expectedPrefix}{title}", RunGitInRepo("log", "-1", "--pretty=%B"));
    }

    [Fact]
    public async Task MergeTask_StagesAllChanges_BeforeCommit()
    {
        const string branchName = "task/test-stage-before-commit";
        CreateFeatureBranchWithCommit(branchName, "staged.txt", "staging flow verification");

        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: branchName);
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        var trace = File.ReadAllLines(_gitTracePath);
        var mergeIndex = Array.FindLastIndex(trace, line => line.StartsWith("merge --squash ", StringComparison.Ordinal));
        var addIndex = Array.FindLastIndex(trace, line => string.Equals(line, "add -A", StringComparison.Ordinal));
        var commitIndex = Array.FindLastIndex(trace, line => line.StartsWith("commit -m ", StringComparison.Ordinal));

        Assert.True(mergeIndex >= 0, "Expected squash merge to be traced.");
        Assert.True(addIndex > mergeIndex, "Expected git add -A after squash merge.");
        Assert.True(commitIndex > addIndex, "Expected commit after git add -A.");
    }

    [Fact]
    public async Task MergeTask_Failure_RestoresApprovedStatus()
    {
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: "task/missing-branch");
        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskId }, "reviewer-1", "Socrates", "Reviewer");

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Merge failed", result.Error!);

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var task = await runtime.GetTaskAsync(taskId);

        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Approved, task!.Status);
        Assert.Null(task.MergeCommitSha);
    }

    [Fact]
    public async Task BranchWorkflow_LinkedBreakout_CanProgressFromInReviewToMerged()
    {
        const string branchName = "task/full-workflow-abc123";
        CreateFeatureBranchWithCommit(branchName, "workflow.txt", "full workflow change");

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await runtime.InitializeAsync();
        await EnsureRoom(db, "room-1");

        var taskResult = await runtime.CreateTaskAsync(new TaskAssignmentRequest(
            Title: "Full Workflow Task",
            Description: "Exercise branch workflow end to end",
            SuccessCriteria: "Transitions through review and merge",
            RoomId: "main",
            PreferredRoles: []));
        await runtime.UpdateTaskBranchAsync(taskResult.Task.Id, branchName);

        var breakout = await runtime.CreateBreakoutRoomAsync("main", "engineer-1", "BR: Full Workflow Task");
        await runtime.SetBreakoutTaskIdAsync(breakout.Id, taskResult.Task.Id);

        var inReviewTask = await runtime.TransitionBreakoutTaskToInReviewAsync(breakout.Id);
        Assert.NotNull(inReviewTask);
        Assert.Equal(TaskStatus.InReview, inReviewTask!.Status);

        var approvedTask = await runtime.ApproveTaskAsync(taskResult.Task.Id, "reviewer-1", "Looks good.");
        Assert.Equal(TaskStatus.Approved, approvedTask.Status);

        var handler = new MergeTaskHandler(_gitService);
        var (cmd, ctx) = MakeCommand("MERGE_TASK",
            new() { ["taskId"] = taskResult.Task.Id }, "reviewer-1", "Socrates", "Reviewer");

        var mergeResult = await handler.ExecuteAsync(cmd, ctx);

        Assert.True(
            mergeResult.Status == CommandStatus.Success,
            $"MERGE_TASK failed: {mergeResult.Error}");

        using var verificationScope = _serviceProvider.CreateScope();
        var verificationRuntime = verificationScope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var mergedTask = await verificationRuntime.GetTaskAsync(taskResult.Task.Id);
        Assert.NotNull(mergedTask);
        Assert.Equal(TaskStatus.Completed, mergedTask!.Status);
        Assert.False(string.IsNullOrWhiteSpace(mergedTask.MergeCommitSha));
    }

    // ── Parser Test ─────────────────────────────────────────────

    [Fact]
    public void CommandParser_RecognizesMergeTask()
    {
        var parser = new CommandParser();
        var result = parser.Parse("MERGE_TASK: task-123");

        Assert.Single(result.Commands);
        Assert.Equal("MERGE_TASK", result.Commands[0].Command);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private async Task<string> CreateTestTask(
        string status = "Active",
        string? branchName = "task/test-abc123",
        string? assignedAgentId = null,
        string title = "Test Task",
        TaskType taskType = TaskType.Feature)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await EnsureRoom(db, "room-1");

        var taskId = $"task-{Guid.NewGuid():N}"[..20];
        db.Tasks.Add(new Data.Entities.TaskEntity
        {
            Id = taskId,
            Title = title,
            Description = "Test task description",
            SuccessCriteria = "It works",
            Status = status,
            Type = taskType.ToString(),
            CurrentPhase = "Implementation",
            RoomId = "room-1",
            BranchName = branchName,
            AssignedAgentId = assignedAgentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return taskId;
    }

    private static async Task EnsureRoom(AgentAcademyDbContext db, string roomId)
    {
        var room = await db.Rooms.FindAsync(roomId);
        if (room == null)
        {
            db.Rooms.Add(new Data.Entities.RoomEntity
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

    private static string CreateGitWrapper(string repoRoot, string tracePath)
    {
        var wrapperPath = Path.Combine(repoRoot, "git-wrapper.sh");
        File.WriteAllText(wrapperPath,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> "{{tracePath}}"
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

    private string RunGitInRepo(params string[] args)
        => RunGit(_repoRoot, args);

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
            ?? throw new InvalidOperationException("Failed to start git process for test repository");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed in {workingDirectory}: {stderr.Trim()}");

        return stdout.Trim();
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(
        string commandName,
        Dictionary<string, string> args,
        string agentId = "reviewer-1",
        string agentName = "Socrates",
        string agentRole = "Reviewer")
    {
        var scope = _serviceProvider.CreateScope();

        var command = new CommandEnvelope(
            Command: commandName,
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

    // ── Breakout Task Identity & Branch Persistence Tests ───────

    [Fact]
    public async Task EnsureTaskForBreakout_CreatesAndLinksTaskForBreakout()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await EnsureRoom(db, "room-1");
        var breakout = await runtime.CreateBreakoutRoomAsync("room-1", "engineer-1", "BR: Fix Login Bug");

        var taskId = await runtime.EnsureTaskForBreakoutAsync(
            breakout.Id, "Fix Login Bug", "desc", "engineer-1", "room-1");

        var task = await runtime.GetTaskAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal("Fix Login Bug", task.Title);
        Assert.Equal(taskId, await runtime.GetBreakoutTaskIdAsync(breakout.Id));
    }

    [Fact]
    public async Task EnsureTaskForBreakout_ReturnsExistingLinkedTaskForSameBreakout()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await EnsureRoom(db, "room-1");
        var breakout = await runtime.CreateBreakoutRoomAsync("room-1", "engineer-1", "BR: Fix Login Bug");

        var firstTaskId = await runtime.EnsureTaskForBreakoutAsync(
            breakout.Id, "Fix Login Bug", "desc", "engineer-1", "room-1");
        var secondTaskId = await runtime.EnsureTaskForBreakoutAsync(
            breakout.Id, "Different Title", "different desc", "engineer-1", "room-1");

        Assert.Equal(firstTaskId, secondTaskId);
    }

    [Fact]
    public async Task EnsureTaskForBreakout_OverlappingBreakoutsGetDistinctTasksAndBranches()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await EnsureRoom(db, "room-1");
        await EnsureRoom(db, "room-2");
        var breakoutOne = await runtime.CreateBreakoutRoomAsync("room-1", "engineer-1", "BR: Task One");
        var breakoutTwo = await runtime.CreateBreakoutRoomAsync("room-2", "engineer-1", "BR: Task Two");

        var taskOneId = await runtime.EnsureTaskForBreakoutAsync(
            breakoutOne.Id, "Task One", "description one", "engineer-1", "room-1");

        // Simulate overlapping setup: task one exists but has not yet been assigned a branch.
        var taskTwoId = await runtime.EnsureTaskForBreakoutAsync(
            breakoutTwo.Id, "Task Two", "description two", "engineer-1", "room-2");

        Assert.NotEqual(taskOneId, taskTwoId);

        await runtime.UpdateTaskBranchAsync(taskOneId, "task/task-one-abc123");
        await runtime.UpdateTaskBranchAsync(taskTwoId, "task/task-two-def456");

        var taskOne = await runtime.GetTaskAsync(taskOneId);
        var taskTwo = await runtime.GetTaskAsync(taskTwoId);
        Assert.Equal("task/task-one-abc123", taskOne!.BranchName);
        Assert.Equal("task/task-two-def456", taskTwo!.BranchName);
        Assert.Equal(taskOneId, await runtime.GetBreakoutTaskIdAsync(breakoutOne.Id));
        Assert.Equal(taskTwoId, await runtime.GetBreakoutTaskIdAsync(breakoutTwo.Id));
    }

    [Fact]
    public async Task SetBreakoutTaskId_PersistsLink()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await EnsureRoom(db, "room-1");

        var br = await runtime.CreateBreakoutRoomAsync("room-1", "engineer-1", "BR: Test");
        await runtime.SetBreakoutTaskIdAsync(br.Id, "task-123");

        var storedId = await runtime.GetBreakoutTaskIdAsync(br.Id);
        Assert.Equal("task-123", storedId);
    }

    [Fact]
    public async Task SetBreakoutTaskId_DifferentExistingLink_Throws()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        await EnsureRoom(db, "room-1");

        var breakout = await runtime.CreateBreakoutRoomAsync("room-1", "engineer-1", "BR: Test");
        await runtime.SetBreakoutTaskIdAsync(breakout.Id, "task-123");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.SetBreakoutTaskIdAsync(breakout.Id, "task-456"));

        Assert.Contains("already linked", ex.Message);
    }

    [Fact]
    public async Task UpdateTaskBranch_DifferentExistingBranch_Throws()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Active), branchName: null);

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await runtime.UpdateTaskBranchAsync(taskId, "task/original-abc123");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.UpdateTaskBranchAsync(taskId, "task/reassigned-def456"));

        Assert.Contains("cannot be reassigned", ex.Message);
    }

    [Fact]
    public async Task CompleteTask_PersistsMergeCommitSha()
    {
        var taskId = await CreateTestTask(status: nameof(TaskStatus.Approved));

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        var result = await runtime.CompleteTaskAsync(
            taskId, commitCount: 1, mergeCommitSha: "abc123def456");

        Assert.Equal(TaskStatus.Completed, result.Status);
        Assert.Equal("abc123def456", result.MergeCommitSha);
    }

    [Fact]
    public async Task MergeTaskResult_IncludesMergeSha()
    {
        // This test validates the result schema — the handler returns mergeSha
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.Approved),
            branchName: "task/test-branch-abc123");

        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        // Simulate what MergeTaskHandler does after merge: complete with SHA
        var result = await runtime.CompleteTaskAsync(
            taskId, commitCount: 1, mergeCommitSha: "deadbeef12345678");

        Assert.Equal("deadbeef12345678", result.MergeCommitSha);
        Assert.Equal(TaskStatus.Completed, result.Status);
        Assert.Equal(1, result.CommitCount);
    }

    [Fact]
    public async Task InReviewTransition_TaskWithBranch_CanBeApproved()
    {
        // Task starts InReview (what HandleBreakoutCompleteAsync sets)
        var taskId = await CreateTestTask(
            status: nameof(TaskStatus.InReview),
            branchName: "task/feature-abc123");

        // APPROVE_TASK should work on InReview tasks
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        var approved = await runtime.ApproveTaskAsync(taskId, "reviewer-1", null);

        Assert.Equal(TaskStatus.Approved, approved.Status);
    }
}
