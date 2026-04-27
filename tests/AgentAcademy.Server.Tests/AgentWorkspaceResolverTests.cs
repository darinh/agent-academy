using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="AgentWorkspaceResolver"/> — the per-agent workspace
/// router that closes P1.9 blocker D. Verifies the routing rules called out
/// in the resolver's contract: zero / exactly-one / multiple claimed tasks,
/// scoping by room/workspace, and graceful degradation when worktree
/// provisioning fails.
/// </summary>
public sealed class AgentWorkspaceResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AgentAcademyDbContext> _dbOptions;

    public AgentWorkspaceResolverTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = new AgentAcademyDbContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ResolveAsync_NoClaimedTask_ReturnsRoomWorkspacePath()
    {
        await using var db = new AgentAcademyDbContext(_dbOptions);
        var worktree = Substitute.For<IWorktreeService>();
        var resolver = new AgentWorkspaceResolver(db, worktree, NullLogger<AgentWorkspaceResolver>.Instance);

        var result = await resolver.ResolveAsync("agent-1", "room-1", "/repo/develop");

        Assert.Equal("/repo/develop", result);
        await worktree.DidNotReceive().CreateWorktreeAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ResolveAsync_OneClaimedActiveTaskWithBranch_ReturnsWorktreePath()
    {
        await using var db = new AgentAcademyDbContext(_dbOptions);
        await SeedTaskAsync(db, "task-1", "agent-1", "room-1", "/repo/develop",
            status: nameof(Shared.Models.TaskStatus.Active),
            branchName: "task/feature-x");

        var worktree = Substitute.For<IWorktreeService>();
        worktree.CreateWorktreeAsync("task/feature-x").Returns(
            new WorktreeInfo("task/feature-x", "/repo/.worktrees/task_feature-x", DateTimeOffset.UtcNow));

        var resolver = new AgentWorkspaceResolver(db, worktree, NullLogger<AgentWorkspaceResolver>.Instance);

        var result = await resolver.ResolveAsync("agent-1", "room-1", "/repo/develop");

        Assert.Equal("/repo/.worktrees/task_feature-x", result);
        await worktree.Received(1).CreateWorktreeAsync("task/feature-x");
    }

    [Fact]
    public async Task ResolveAsync_TaskWithoutBranch_FallsBackToRoomWorkspacePath()
    {
        await using var db = new AgentAcademyDbContext(_dbOptions);
        await SeedTaskAsync(db, "task-1", "agent-1", "room-1", "/repo/develop",
            status: nameof(Shared.Models.TaskStatus.Active),
            branchName: null);

        var worktree = Substitute.For<IWorktreeService>();
        var resolver = new AgentWorkspaceResolver(db, worktree, NullLogger<AgentWorkspaceResolver>.Instance);

        var result = await resolver.ResolveAsync("agent-1", "room-1", "/repo/develop");

        Assert.Equal("/repo/develop", result);
        await worktree.DidNotReceive().CreateWorktreeAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData(nameof(Shared.Models.TaskStatus.Completed))]
    [InlineData(nameof(Shared.Models.TaskStatus.Cancelled))]
    public async Task ResolveAsync_TerminalStatus_FallsBackToRoomWorkspacePath(string terminalStatus)
    {
        await using var db = new AgentAcademyDbContext(_dbOptions);
        await SeedTaskAsync(db, "task-1", "agent-1", "room-1", "/repo/develop",
            status: terminalStatus,
            branchName: "task/finished");

        var worktree = Substitute.For<IWorktreeService>();
        var resolver = new AgentWorkspaceResolver(db, worktree, NullLogger<AgentWorkspaceResolver>.Instance);

        var result = await resolver.ResolveAsync("agent-1", "room-1", "/repo/develop");

        Assert.Equal("/repo/develop", result);
    }

    [Theory]
    [InlineData(nameof(Shared.Models.TaskStatus.Blocked))]
    [InlineData(nameof(Shared.Models.TaskStatus.AwaitingValidation))]
    [InlineData(nameof(Shared.Models.TaskStatus.InReview))]
    [InlineData(nameof(Shared.Models.TaskStatus.ChangesRequested))]
    public async Task ResolveAsync_NonTerminalNonActiveStatus_StillRoutesToWorktree(string status)
    {
        await using var db = new AgentAcademyDbContext(_dbOptions);
        await SeedTaskAsync(db, "task-1", "agent-1", "room-1", "/repo/develop",
            status: status,
            branchName: "task/in-progress");

        var worktree = Substitute.For<IWorktreeService>();
        worktree.CreateWorktreeAsync("task/in-progress").Returns(
            new WorktreeInfo("task/in-progress", "/repo/.worktrees/task_in-progress", DateTimeOffset.UtcNow));

        var resolver = new AgentWorkspaceResolver(db, worktree, NullLogger<AgentWorkspaceResolver>.Instance);

        var result = await resolver.ResolveAsync("agent-1", "room-1", "/repo/develop");

        Assert.Equal("/repo/.worktrees/task_in-progress", result);
    }

    [Fact]
    public async Task ResolveAsync_MultipleClaimedTasks_FailsClosedAndReturnsRoomPath()
    {
        await using var db = new AgentAcademyDbContext(_dbOptions);
        await SeedTaskAsync(db, "task-1", "agent-1", "room-1", "/repo/develop",
            status: nameof(Shared.Models.TaskStatus.Active),
            branchName: "task/first");
        await SeedTaskAsync(db, "task-2", "agent-1", "room-1", "/repo/develop",
            status: nameof(Shared.Models.TaskStatus.Active),
            branchName: "task/second");

        var worktree = Substitute.For<IWorktreeService>();
        var resolver = new AgentWorkspaceResolver(db, worktree, NullLogger<AgentWorkspaceResolver>.Instance);

        var result = await resolver.ResolveAsync("agent-1", "room-1", "/repo/develop");

        Assert.Equal("/repo/develop", result);
        // Critically: NO worktree provisioning when ambiguous — silently picking
        // one of N would be the wrong worktree half the time.
        await worktree.DidNotReceive().CreateWorktreeAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ResolveAsync_TaskInDifferentWorkspace_DoesNotHijackRouting()
    {
        await using var db = new AgentAcademyDbContext(_dbOptions);
        // Stale claim in a different workspace must NOT redirect this room's writes.
        await SeedTaskAsync(db, "task-1", "agent-1", "other-room", "/some/other/workspace",
            status: nameof(Shared.Models.TaskStatus.Active),
            branchName: "task/elsewhere");

        var worktree = Substitute.For<IWorktreeService>();
        var resolver = new AgentWorkspaceResolver(db, worktree, NullLogger<AgentWorkspaceResolver>.Instance);

        var result = await resolver.ResolveAsync("agent-1", "room-1", "/repo/develop");

        Assert.Equal("/repo/develop", result);
        await worktree.DidNotReceive().CreateWorktreeAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ResolveAsync_TaskScopedByRoomWhenWorkspaceMissing_StillRoutes()
    {
        await using var db = new AgentAcademyDbContext(_dbOptions);
        // Task created via CREATE_TASK_ITEM may have no WorkspacePath but is
        // scoped by RoomId. Must still route when its room matches the call.
        await SeedTaskAsync(db, "task-1", "agent-1", "room-1", workspacePath: null,
            status: nameof(Shared.Models.TaskStatus.Active),
            branchName: "task/room-scoped");

        var worktree = Substitute.For<IWorktreeService>();
        worktree.CreateWorktreeAsync("task/room-scoped").Returns(
            new WorktreeInfo("task/room-scoped", "/repo/.worktrees/task_room-scoped", DateTimeOffset.UtcNow));

        var resolver = new AgentWorkspaceResolver(db, worktree, NullLogger<AgentWorkspaceResolver>.Instance);

        var result = await resolver.ResolveAsync("agent-1", "room-1", "/repo/develop");

        Assert.Equal("/repo/.worktrees/task_room-scoped", result);
    }

    [Fact]
    public async Task ResolveAsync_WorktreeProvisioningThrows_FallsBackToRoomPath()
    {
        await using var db = new AgentAcademyDbContext(_dbOptions);
        await SeedTaskAsync(db, "task-1", "agent-1", "room-1", "/repo/develop",
            status: nameof(Shared.Models.TaskStatus.Active),
            branchName: "task/branch-deleted");

        var worktree = Substitute.For<IWorktreeService>();
        worktree.CreateWorktreeAsync("task/branch-deleted")
            .Returns<WorktreeInfo>(_ => throw new InvalidOperationException("branch missing"));

        var resolver = new AgentWorkspaceResolver(db, worktree, NullLogger<AgentWorkspaceResolver>.Instance);

        var result = await resolver.ResolveAsync("agent-1", "room-1", "/repo/develop");

        Assert.Equal("/repo/develop", result);
    }

    [Fact]
    public async Task ResolveAsync_DifferentAgentClaimedTask_DoesNotRoute()
    {
        await using var db = new AgentAcademyDbContext(_dbOptions);
        await SeedTaskAsync(db, "task-1", "OTHER-AGENT", "room-1", "/repo/develop",
            status: nameof(Shared.Models.TaskStatus.Active),
            branchName: "task/not-mine");

        var worktree = Substitute.For<IWorktreeService>();
        var resolver = new AgentWorkspaceResolver(db, worktree, NullLogger<AgentWorkspaceResolver>.Instance);

        var result = await resolver.ResolveAsync("agent-1", "room-1", "/repo/develop");

        Assert.Equal("/repo/develop", result);
        await worktree.DidNotReceive().CreateWorktreeAsync(Arg.Any<string>());
    }

    private static async Task SeedTaskAsync(
        AgentAcademyDbContext db,
        string id, string assignedAgentId, string? roomId, string? workspacePath,
        string status, string? branchName)
    {
        // Seed FK targets (Room / Workspace) idempotently so the FK constraints
        // hold under the in-memory SQLite schema.
        if (workspacePath is not null && !await db.Workspaces.AnyAsync(w => w.Path == workspacePath))
        {
            db.Workspaces.Add(new WorkspaceEntity
            {
                Path = workspacePath,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
        }
        if (roomId is not null && !await db.Rooms.AnyAsync(r => r.Id == roomId))
        {
            db.Rooms.Add(new RoomEntity
            {
                Id = roomId,
                Name = $"Room {roomId}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                WorkspacePath = workspacePath,
            });
        }
        await db.SaveChangesAsync();

        db.Tasks.Add(new TaskEntity
        {
            Id = id,
            Title = $"Task {id}",
            Description = "test",
            Status = status,
            Type = nameof(TaskType.Feature),
            Priority = (int)TaskPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            RoomId = roomId,
            WorkspacePath = workspacePath,
            AssignedAgentId = assignedAgentId,
            BranchName = branchName,
        });
        await db.SaveChangesAsync();
    }
}
