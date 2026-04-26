using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Targeted tests for the lazy branch + worktree provisioning behavior added
/// to <see cref="ClaimTaskHandler"/> for P1.9-blocker-B. Tasks created via
/// CREATE_TASK_ITEM start without git resources; without provisioning at claim
/// time, downstream CREATE_PR fails with "Task has no branch" and breakout
/// agents contaminate the develop checkout.
/// </summary>
public class ClaimTaskProvisioningTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IGitService _gitService;
    private readonly IWorktreeService _worktreeService;
    private readonly AgentCatalogOptions _catalog;

    public ClaimTaskProvisioningTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _gitService = Substitute.For<IGitService>();
        _worktreeService = Substitute.For<IWorktreeService>();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main",
            Agents:
            [
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "p", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true)
            ]);

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddSingleton(_gitService);
        services.AddSingleton(_worktreeService);
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ClaimTask_ProvisionsBranchAndWorktree_WhenTaskHasNoBranch()
    {
        var taskId = await CreateTask(branchName: null);
        const string newBranch = "task/test-task-abc123";
        _gitService.CreateTaskBranchAsync(Arg.Any<string>()).Returns(newBranch);
        _worktreeService.CreateWorktreeAsync(newBranch).Returns(
            new WorktreeInfo(newBranch, "/tmp/wt", DateTimeOffset.UtcNow));

        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand(taskId);

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(newBranch, result.Result!["branchName"]!.ToString());
        Assert.False(result.Result.ContainsKey("warning"));
        await _gitService.Received(1).CreateTaskBranchAsync(Arg.Any<string>());
        await _gitService.Received(1).ReturnToDevelopAsync(newBranch);
        await _worktreeService.Received(1).CreateWorktreeAsync(newBranch);

        await using var db = NewDbContext();
        var entity = await db.Tasks.FindAsync(taskId);
        Assert.Equal(newBranch, entity!.BranchName);
    }

    [Fact]
    public async Task ClaimTask_DoesNotCreateNewBranch_WhenTaskAlreadyHasBranch()
    {
        const string existingBranch = "task/already-set-deadbe";
        var taskId = await CreateTask(branchName: existingBranch);
        _worktreeService.CreateWorktreeAsync(existingBranch).Returns(
            new WorktreeInfo(existingBranch, "/tmp/wt", DateTimeOffset.UtcNow));

        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand(taskId);

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        // CreateTaskBranchAsync must NOT be called for tasks with existing branches
        await _gitService.DidNotReceive().CreateTaskBranchAsync(Arg.Any<string>());
        // But CreateWorktreeAsync IS called (idempotent ensure-worktree-exists)
        await _worktreeService.Received(1).CreateWorktreeAsync(existingBranch);
    }

    [Fact]
    public async Task ClaimTask_SucceedsWithWarning_WhenBranchProvisioningFails()
    {
        var taskId = await CreateTask(branchName: null);
        _gitService.CreateTaskBranchAsync(Arg.Any<string>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("git: cannot create branch"));

        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand(taskId);

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.True(result.Result!.ContainsKey("warning"),
            "Failed provisioning must surface a warning to the agent");
        Assert.Contains("provisioning failed", result.Result["warning"]!.ToString()!,
            StringComparison.OrdinalIgnoreCase);

        // Task remains claimed (the claim succeeded before provisioning was attempted)
        await using var db = NewDbContext();
        var entity = await db.Tasks.FindAsync(taskId);
        Assert.Equal("engineer-1", entity!.AssignedAgentId);
        Assert.Null(entity.BranchName);
    }

    [Fact]
    public async Task ClaimTask_RecordsBranchEvenWhenWorktreeFails()
    {
        var taskId = await CreateTask(branchName: null);
        const string newBranch = "task/wt-fail-fab123";
        _gitService.CreateTaskBranchAsync(Arg.Any<string>()).Returns(newBranch);
        _worktreeService.CreateWorktreeAsync(newBranch)
            .Returns<Task<WorktreeInfo>>(_ => throw new IOException("disk full"));

        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand(taskId);

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.True(result.Result!.ContainsKey("warning"));

        // The branch name should still be recorded so a re-claim doesn't try
        // to create a duplicate branch.
        await using var db = NewDbContext();
        var entity = await db.Tasks.FindAsync(taskId);
        Assert.Equal(newBranch, entity!.BranchName);
    }

    [Fact]
    public async Task ClaimTask_IdempotentReclaim_DoesNotCreateSecondBranch()
    {
        var taskId = await CreateTask(branchName: null);
        const string newBranch = "task/idempotent-cab456";
        _gitService.CreateTaskBranchAsync(Arg.Any<string>()).Returns(newBranch);
        _worktreeService.CreateWorktreeAsync(newBranch).Returns(
            new WorktreeInfo(newBranch, "/tmp/wt", DateTimeOffset.UtcNow));

        var handler = new ClaimTaskHandler();
        var (cmd1, ctx1) = MakeCommand(taskId);
        var (cmd2, ctx2) = MakeCommand(taskId);

        var first = await handler.ExecuteAsync(cmd1, ctx1);
        var second = await handler.ExecuteAsync(cmd2, ctx2);

        Assert.Equal(CommandStatus.Success, first.Status);
        Assert.Equal(CommandStatus.Success, second.Status);
        // CreateTaskBranchAsync must only be called once (the second claim sees the branch)
        await _gitService.Received(1).CreateTaskBranchAsync(Arg.Any<string>());
        await _gitService.Received(1).ReturnToDevelopAsync(newBranch);
        // CreateWorktreeAsync called twice: once for create, once for the
        // existing-branch ensure path on re-claim. Both calls are for the same branch.
        await _worktreeService.Received(2).CreateWorktreeAsync(newBranch);
    }

    [Fact]
    public async Task ClaimTask_RestoresDevelopCheckout_WhenBranchCreationLeavesUsOnTaskBranch()
    {
        // Branch creation succeeds (and leaves checkout on the new branch via
        // `git checkout -b`), then ReturnToDevelopAsync itself throws — we're
        // stuck on the task branch. The handler MUST attempt restoration in
        // the catch block, otherwise we re-introduce cross-task contamination.
        var taskId = await CreateTask(branchName: null);
        const string newBranch = "task/restore-test-de4d12";
        _gitService.CreateTaskBranchAsync(Arg.Any<string>()).Returns(newBranch);
        var returnCallCount = 0;
        _gitService.ReturnToDevelopAsync(newBranch).Returns(_ =>
        {
            returnCallCount++;
            if (returnCallCount == 1)
                throw new InvalidOperationException("git: stash failed");
            return Task.CompletedTask;
        });

        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand(taskId);

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.True(result.Result!.ContainsKey("warning"));
        // Called twice: first attempt threw, catch-block defensive call ran.
        await _gitService.Received(2).ReturnToDevelopAsync(newBranch);
    }

    [Fact]
    public async Task ClaimTask_DoesNotDoubleStash_WhenWorktreeCreationFailsAfterSuccessfulReturnToDevelop()
    {
        // Regression guard: ReturnToDevelopAsync stashes unconditionally (see
        // GitService.ReturnToDevelopInternalAsync), so calling it a second
        // time when already on develop would stash unrelated develop changes
        // under the task-branch label. The handler must skip the catch-block
        // restoration when the happy-path return already succeeded.
        var taskId = await CreateTask(branchName: null);
        const string newBranch = "task/no-double-stash-bad123";
        _gitService.CreateTaskBranchAsync(Arg.Any<string>()).Returns(newBranch);
        _gitService.ReturnToDevelopAsync(newBranch).Returns(Task.CompletedTask);
        _worktreeService.CreateWorktreeAsync(newBranch)
            .Returns<Task<WorktreeInfo>>(_ => throw new IOException("disk full"));

        var handler = new ClaimTaskHandler();
        var (cmd, ctx) = MakeCommand(taskId);

        var result = await handler.ExecuteAsync(cmd, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.True(result.Result!.ContainsKey("warning"));
        // ReturnToDevelopAsync called exactly ONCE — the catch block must not
        // re-call it because the happy-path call already returned us to develop.
        await _gitService.Received(1).ReturnToDevelopAsync(newBranch);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private AgentAcademyDbContext NewDbContext()
    {
        var opts = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AgentAcademyDbContext(opts);
    }

    private async Task<string> CreateTask(string? branchName)
    {
        await using var db = NewDbContext();
        var id = Guid.NewGuid().ToString("N");
        db.Tasks.Add(new TaskEntity
        {
            Id = id,
            Title = "Implement /api/version endpoint",
            Description = "Test task",
            Status = "Active",
            Type = "Feature",
            CurrentPhase = "Planning",
            CurrentPlan = string.Empty,
            ValidationStatus = "Ready",
            ValidationSummary = string.Empty,
            ImplementationStatus = "NotStarted",
            ImplementationSummary = string.Empty,
            PreferredRoles = "[]",
            BranchName = branchName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private (CommandEnvelope command, CommandContext context) MakeCommand(string taskId)
    {
        var scope = _serviceProvider.CreateScope();
        var ctx = new CommandContext(
            AgentId: "engineer-1",
            AgentName: "Hephaestus",
            AgentRole: "SoftwareEngineer",
            RoomId: "room-1",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider);
        var cmd = new CommandEnvelope(
            Command: "CLAIM_TASK",
            Args: new Dictionary<string, object?> { ["taskId"] = taskId },
            Status: CommandStatus.Success,
            Result: null,
            Error: null,
            CorrelationId: $"cmd-{Guid.NewGuid():N}",
            Timestamp: DateTime.UtcNow,
            ExecutedBy: "engineer-1");
        return (cmd, ctx);
    }
}
