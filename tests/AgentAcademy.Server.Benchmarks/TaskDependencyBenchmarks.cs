using BenchmarkDotNet.Attributes;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="TaskDependencyService"/> — BFS cycle detection
/// and dependency queries over an EF Core + SQLite graph.
/// Uses in-memory SQLite for isolation. Activity publishing is no-op to prevent
/// unbounded state growth across iterations.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
public class TaskDependencyBenchmarks
{
    private SqliteConnection _connection = default!;
    private AgentAcademyDbContext _db = default!;
    private TaskDependencyService _sut = default!;
    private string _roomId = default!;

    [Params(10, 50, 200)]
    public int TaskCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        // Seed workspace
        _db.Workspaces.Add(new WorkspaceEntity
        {
            Path = "/bench", ProjectName = "bench", IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        // Seed room
        _roomId = "room-bench";
        _db.Rooms.Add(new RoomEntity
        {
            Id = _roomId, Name = "Benchmark Room",
            WorkspacePath = "/bench",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        // Seed tasks
        for (var i = 0; i < TaskCount; i++)
        {
            _db.Tasks.Add(new TaskEntity
            {
                Id = $"task-{i}",
                Title = $"Task {i}",
                Description = $"Benchmark task {i}",
                RoomId = _roomId,
                WorkspacePath = "/bench",
                Status = "Active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        // Create a linear dependency chain: task-0 → task-1 → task-2 → ... → task-(N/2)
        var chainLength = Math.Min(TaskCount / 2, TaskCount - 2);
        for (var i = 0; i < chainLength; i++)
        {
            _db.TaskDependencies.Add(new TaskDependencyEntity
            {
                TaskId = $"task-{i}",
                DependsOnTaskId = $"task-{i + 1}",
                CreatedAt = DateTime.UtcNow
            });
        }

        _db.SaveChanges();

        // No-op publisher prevents activity event accumulation across iterations
        _sut = new TaskDependencyService(_db, NullLogger<TaskDependencyService>.Instance, new NoOpActivityPublisher());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Add a dependency that requires BFS cycle check but does NOT create a cycle.
    /// Measures the cost of loading all edges + BFS traversal + DB write.
    /// We remove the dependency after each iteration to keep the benchmark repeatable.
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("CycleCheck")]
    public async Task AddDependencyNoCycle()
    {
        // task-(N-1) depends on task-(N-2): no cycle because task-(N-1) is a leaf
        var lastIdx = TaskCount - 1;
        var secondLastIdx = TaskCount - 2;
        try
        {
            await _sut.AddDependencyAsync($"task-{lastIdx}", $"task-{secondLastIdx}");
        }
        catch (InvalidOperationException)
        {
            // Dependency may already exist from a previous iteration
        }
        finally
        {
            // Clean up for next iteration
            var dep = await _db.TaskDependencies
                .FirstOrDefaultAsync(d => d.TaskId == $"task-{lastIdx}" && d.DependsOnTaskId == $"task-{secondLastIdx}");
            if (dep != null)
            {
                _db.TaskDependencies.Remove(dep);
                await _db.SaveChangesAsync();
            }
        }
    }

    /// <summary>
    /// Attempt to add a dependency that WOULD create a cycle (rejected by BFS).
    /// Measures worst-case BFS traversal time when the full chain is explored.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("CycleCheck")]
    public async Task AddDependencyWithCycle()
    {
        // Trying to make the end of the chain depend on the start would create a cycle
        var chainEnd = Math.Min(TaskCount / 2, TaskCount - 2);
        try
        {
            await _sut.AddDependencyAsync($"task-{chainEnd}", "task-0");
        }
        catch (InvalidOperationException)
        {
            // Expected: cycle detected
        }
    }

    /// <summary>
    /// Query blocking tasks for a task in the middle of the chain.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Query")]
    public async Task<object> GetBlockingTasks()
    {
        return await _sut.GetBlockingTasksAsync("task-0");
    }

    /// <summary>
    /// Get full dependency info (depends-on + depended-on-by) for a task.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Query")]
    public async Task<object> GetDependencyInfo()
    {
        var midpoint = TaskCount / 4;
        return await _sut.GetDependencyInfoAsync($"task-{midpoint}");
    }

    /// <summary>
    /// No-op activity publisher that avoids accumulating ActivityEvent rows
    /// across benchmark iterations, keeping the DB state stable.
    /// </summary>
    private sealed class NoOpActivityPublisher : IActivityPublisher
    {
        public ActivityEvent Publish(ActivityEventType type, string? roomId, string? actorId,
            string? taskId, string message, string? correlationId = null,
            ActivitySeverity severity = ActivitySeverity.Info) =>
            new("noop", type, severity, roomId, actorId, taskId, message, correlationId, DateTime.UtcNow);

        public Task PublishThinkingAsync(AgentDefinition agent, string roomId) =>
            Task.CompletedTask;

        public Task PublishFinishedAsync(AgentDefinition agent, string roomId) =>
            Task.CompletedTask;

        public IReadOnlyList<ActivityEvent> GetRecentActivity() => [];

        public Action Subscribe(Action<ActivityEvent> callback) => () => { };
    }
}
