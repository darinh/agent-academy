using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for TaskDependencyService: CRUD, cycle detection, blocking queries,
/// and integration with TaskLifecycleService/TaskQueryService status transitions.
/// </summary>
public class TaskDependencyServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ActivityPublisher _activity;
    private readonly TaskDependencyService _sut;
    private readonly TaskLifecycleService _lifecycle;
    private readonly TaskQueryService _queries;
    private readonly AgentCatalogOptions _catalog;

    public TaskDependencyServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _catalog = new AgentCatalogOptions("main", "Main Room",
            new List<AgentDefinition>
            {
                new("eng-1", "Hephaestus", "SoftwareEngineer", "Engineer", "prompt", null, [], [], true),
                new("eng-2", "Athena", "SoftwareEngineer", "Engineer", "prompt", null, [], [], true),
            });

        var bus = new ActivityBroadcaster();
        _activity = new ActivityPublisher(_db, bus);

        _sut = new TaskDependencyService(_db, NullLogger<TaskDependencyService>.Instance, _activity);
        _lifecycle = new TaskLifecycleService(
            _db, NullLogger<TaskLifecycleService>.Instance, _catalog, _activity, _sut);
        _queries = new TaskQueryService(
            _db, NullLogger<TaskQueryService>.Instance, _catalog, _sut);

        // Seed workspace
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/test", ProjectName = "test", IsActive = true, CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<TaskEntity> CreateTask(string id, string title, string status = "Queued")
    {
        var entity = new TaskEntity
        {
            Id = id,
            Title = title,
            Description = "Test task",
            Status = status,
            CurrentPhase = "Planning",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            WorkspacePath = "/test"
        };
        _db.Tasks.Add(entity);
        await _db.SaveChangesAsync();
        return entity;
    }

    // ── Add Dependency ──────────────────────────────────────────

    [Fact]
    public async Task AddDependency_BasicSuccess()
    {
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B");

        var info = await _sut.AddDependencyAsync("task-b", "task-a");

        Assert.Equal("task-b", info.TaskId);
        Assert.Single(info.DependsOn);
        Assert.Equal("task-a", info.DependsOn[0].TaskId);
        Assert.False(info.DependsOn[0].IsSatisfied);
    }

    [Fact]
    public async Task AddDependency_SelfDependency_Throws()
    {
        await CreateTask("task-a", "Task A");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AddDependencyAsync("task-a", "task-a"));
        Assert.Contains("cannot depend on itself", ex.Message);
    }

    [Fact]
    public async Task AddDependency_DuplicateDependency_Throws()
    {
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B");

        await _sut.AddDependencyAsync("task-b", "task-a");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AddDependencyAsync("task-b", "task-a"));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task AddDependency_TaskNotFound_Throws()
    {
        await CreateTask("task-a", "Task A");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AddDependencyAsync("task-a", "nonexistent"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task AddDependency_CancelledTarget_Throws()
    {
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B", "Cancelled");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AddDependencyAsync("task-a", "task-b"));
        Assert.Contains("cancelled", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task AddDependency_CompletedSource_Throws()
    {
        await CreateTask("task-a", "Task A", "Completed");
        await CreateTask("task-b", "Task B");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AddDependencyAsync("task-a", "task-b"));
        Assert.Contains("completed", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task AddDependency_CompletedTarget_Allowed()
    {
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B", "Completed");

        var info = await _sut.AddDependencyAsync("task-a", "task-b");

        Assert.Single(info.DependsOn);
        Assert.True(info.DependsOn[0].IsSatisfied);
    }

    // ── Cycle Detection ─────────────────────────────────────────

    [Fact]
    public async Task AddDependency_SimpleCycle_Throws()
    {
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B");

        await _sut.AddDependencyAsync("task-b", "task-a");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AddDependencyAsync("task-a", "task-b"));
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public async Task AddDependency_TransitiveCycle_Throws()
    {
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B");
        await CreateTask("task-c", "Task C");

        await _sut.AddDependencyAsync("task-b", "task-a"); // B depends on A
        await _sut.AddDependencyAsync("task-c", "task-b"); // C depends on B

        // A depends on C would create A→C→B→A cycle
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AddDependencyAsync("task-a", "task-c"));
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public async Task AddDependency_DiamondDAG_Allowed()
    {
        // A → B, A → C, B → D, C → D — valid diamond DAG, no cycle
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B");
        await CreateTask("task-c", "Task C");
        await CreateTask("task-d", "Task D");

        await _sut.AddDependencyAsync("task-a", "task-b");
        await _sut.AddDependencyAsync("task-a", "task-c");
        await _sut.AddDependencyAsync("task-b", "task-d");
        await _sut.AddDependencyAsync("task-c", "task-d");

        var info = await _sut.GetDependencyInfoAsync("task-a");
        Assert.Equal(2, info.DependsOn.Count);
    }

    // ── Remove Dependency ───────────────────────────────────────

    [Fact]
    public async Task RemoveDependency_Success()
    {
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B");
        await _sut.AddDependencyAsync("task-b", "task-a");

        var info = await _sut.RemoveDependencyAsync("task-b", "task-a");

        Assert.Empty(info.DependsOn);
    }

    [Fact]
    public async Task RemoveDependency_NotFound_Throws()
    {
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RemoveDependencyAsync("task-b", "task-a"));
        Assert.Contains("No dependency found", ex.Message);
    }

    // ── Blocking Queries ────────────────────────────────────────

    [Fact]
    public async Task HasUnmetDependencies_AllCompleted_ReturnsFalse()
    {
        await CreateTask("task-a", "Task A", "Completed");
        await CreateTask("task-b", "Task B");
        await _sut.AddDependencyAsync("task-b", "task-a");

        Assert.False(await _sut.HasUnmetDependenciesAsync("task-b"));
    }

    [Fact]
    public async Task HasUnmetDependencies_SomeActive_ReturnsTrue()
    {
        await CreateTask("task-a", "Task A", "Active");
        await CreateTask("task-b", "Task B");
        await _sut.AddDependencyAsync("task-b", "task-a");

        Assert.True(await _sut.HasUnmetDependenciesAsync("task-b"));
    }

    [Fact]
    public async Task GetBlockingTasks_ReturnsOnlyIncomplete()
    {
        await CreateTask("task-a", "Task A", "Completed");
        await CreateTask("task-b", "Task B", "Active");
        await CreateTask("task-c", "Task C");
        await _sut.AddDependencyAsync("task-c", "task-a");
        await _sut.AddDependencyAsync("task-c", "task-b");

        var blockers = await _sut.GetBlockingTasksAsync("task-c");

        Assert.Single(blockers);
        Assert.Equal("task-b", blockers[0].TaskId);
    }

    [Fact]
    public async Task GetDependencyInfo_ReturnsBothDirections()
    {
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B");
        await CreateTask("task-c", "Task C");
        await _sut.AddDependencyAsync("task-b", "task-a");
        await _sut.AddDependencyAsync("task-c", "task-a");

        var info = await _sut.GetDependencyInfoAsync("task-a");

        Assert.Empty(info.DependsOn);
        Assert.Equal(2, info.DependedOnBy.Count);
    }

    // ── Batch Query ─────────────────────────────────────────────

    [Fact]
    public async Task GetBatchDependencyIds_ReturnsDepsAndBlockers()
    {
        await CreateTask("task-a", "Task A", "Active");
        await CreateTask("task-b", "Task B", "Completed");
        await CreateTask("task-c", "Task C");
        await _sut.AddDependencyAsync("task-c", "task-a");
        await _sut.AddDependencyAsync("task-c", "task-b");

        var batch = await _sut.GetBatchDependencyIdsAsync(["task-c"]);

        Assert.True(batch.ContainsKey("task-c"));
        Assert.Equal(2, batch["task-c"].DependsOn.Count);
        Assert.Single(batch["task-c"].Blocking); // only task-a is blocking
        Assert.Equal("task-a", batch["task-c"].Blocking[0]);
    }

    // ── Integration: Claim blocked by dependencies ──────────────

    [Fact]
    public async Task ClaimTask_BlockedByDependency_Throws()
    {
        await CreateTask("task-a", "Task A", "Active");
        await CreateTask("task-b", "Task B", "Queued");
        await _sut.AddDependencyAsync("task-b", "task-a");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _lifecycle.ClaimTaskAsync("task-b", "eng-1", "Hephaestus"));
        Assert.Contains("unmet dependencies", ex.Message);
    }

    [Fact]
    public async Task ClaimTask_DependenciesSatisfied_Succeeds()
    {
        await CreateTask("task-a", "Task A", "Completed");
        await CreateTask("task-b", "Task B", "Queued");
        await _sut.AddDependencyAsync("task-b", "task-a");

        var snapshot = await _lifecycle.ClaimTaskAsync("task-b", "eng-1", "Hephaestus");

        Assert.Equal(TaskStatus.Active, snapshot.Status);
        Assert.Equal("eng-1", snapshot.AssignedAgentId);
    }

    [Fact]
    public async Task ClaimTask_NoDependencies_Succeeds()
    {
        await CreateTask("task-a", "Task A", "Queued");

        var snapshot = await _lifecycle.ClaimTaskAsync("task-a", "eng-1", "Hephaestus");

        Assert.Equal(TaskStatus.Active, snapshot.Status);
    }

    // ── Integration: Status transition blocked by dependencies ──

    [Fact]
    public async Task UpdateStatus_ToActive_BlockedByDependency_Throws()
    {
        await CreateTask("task-a", "Task A", "Active");
        await CreateTask("task-b", "Task B", "Queued");
        await _sut.AddDependencyAsync("task-b", "task-a");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _queries.UpdateTaskStatusAsync("task-b", TaskStatus.Active));
        Assert.Contains("unmet dependencies", ex.Message);
    }

    [Fact]
    public async Task UpdateStatus_ToActive_DependenciesSatisfied_Succeeds()
    {
        await CreateTask("task-a", "Task A", "Completed");
        await CreateTask("task-b", "Task B", "Queued");
        await _sut.AddDependencyAsync("task-b", "task-a");

        var snapshot = await _queries.UpdateTaskStatusAsync("task-b", TaskStatus.Active);

        Assert.Equal(TaskStatus.Active, snapshot.Status);
    }

    // ── Integration: TaskSnapshot includes dependency data ──────

    [Fact]
    public async Task GetTask_IncludesDependencyIds()
    {
        await CreateTask("task-a", "Task A", "Active");
        await CreateTask("task-b", "Task B");
        await _sut.AddDependencyAsync("task-b", "task-a");

        var snapshot = await _queries.GetTaskAsync("task-b");

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.DependsOnTaskIds);
        Assert.Single(snapshot.DependsOnTaskIds);
        Assert.Equal("task-a", snapshot.DependsOnTaskIds[0]);
        Assert.NotNull(snapshot.BlockingTaskIds);
        Assert.Single(snapshot.BlockingTaskIds);
    }

    [Fact]
    public async Task GetTasks_IncludesDependencyIds()
    {
        await CreateTask("task-a", "Task A", "Completed");
        await CreateTask("task-b", "Task B");
        await _sut.AddDependencyAsync("task-b", "task-a");

        var tasks = await _queries.GetTasksAsync();
        var taskB = tasks.First(t => t.Id == "task-b");

        Assert.NotNull(taskB.DependsOnTaskIds);
        Assert.Single(taskB.DependsOnTaskIds);
        Assert.Empty(taskB.BlockingTaskIds!); // task-a is completed
    }

    // ── Cascade delete ──────────────────────────────────────────

    [Fact]
    public async Task DeleteTask_CascadeDeletesDependencies()
    {
        await CreateTask("task-a", "Task A");
        await CreateTask("task-b", "Task B");
        await _sut.AddDependencyAsync("task-b", "task-a");

        var entity = await _db.Tasks.FindAsync("task-a");
        _db.Tasks.Remove(entity!);
        await _db.SaveChangesAsync();

        var deps = await _db.TaskDependencies.ToListAsync();
        Assert.Empty(deps);
    }

    // ── UpdatedAt bump ──────────────────────────────────────────

    [Fact]
    public async Task AddDependency_BumpsUpdatedAt()
    {
        var taskB = await CreateTask("task-b", "Task B");
        await CreateTask("task-a", "Task A");
        var before = taskB.UpdatedAt;

        await Task.Delay(10); // ensure time difference
        await _sut.AddDependencyAsync("task-b", "task-a");

        await _db.Entry(taskB).ReloadAsync();
        Assert.True(taskB.UpdatedAt > before);
    }
}
