using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages task dependency relationships (DAG). Validates cycle-freedom,
/// provides blocking queries, and publishes activity events on changes.
/// </summary>
public sealed class TaskDependencyService : ITaskDependencyService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<TaskDependencyService> _logger;
    private readonly IActivityPublisher _activity;

    public TaskDependencyService(
        AgentAcademyDbContext db,
        ILogger<TaskDependencyService> logger,
        IActivityPublisher activity)
    {
        _db = db;
        _logger = logger;
        _activity = activity;
    }

    /// <summary>
    /// Adds a dependency: <paramref name="taskId"/> depends on <paramref name="dependsOnTaskId"/>.
    /// Validates both tasks exist, prevents self-deps, rejects cycles, and rejects
    /// dependencies targeting cancelled tasks.
    /// </summary>
    public async Task<TaskDependencyInfo> AddDependencyAsync(string taskId, string dependsOnTaskId)
    {
        if (string.Equals(taskId, dependsOnTaskId, StringComparison.Ordinal))
            throw new InvalidOperationException("A task cannot depend on itself.");

        var task = await _db.Tasks.FindAsync(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found.");
        var depTask = await _db.Tasks.FindAsync(dependsOnTaskId)
            ?? throw new InvalidOperationException($"Task '{dependsOnTaskId}' not found.");

        // Don't allow dependencies on cancelled tasks (they'll never satisfy)
        if (depTask.Status == nameof(Shared.Models.TaskStatus.Cancelled))
            throw new InvalidOperationException(
                $"Cannot depend on cancelled task '{dependsOnTaskId}'. Remove or replace it.");

        // Don't allow modifying terminal tasks
        if (task.Status is nameof(Shared.Models.TaskStatus.Completed) or nameof(Shared.Models.TaskStatus.Cancelled))
            throw new InvalidOperationException(
                $"Cannot add dependencies to {task.Status.ToLowerInvariant()} task '{taskId}'.");

        // Check for existing dependency
        var exists = await _db.TaskDependencies
            .AnyAsync(d => d.TaskId == taskId && d.DependsOnTaskId == dependsOnTaskId);
        if (exists)
            throw new InvalidOperationException(
                $"Dependency already exists: '{taskId}' → '{dependsOnTaskId}'.");

        // Cycle detection via BFS: would adding this edge create a path from dependsOnTaskId back to taskId?
        if (await WouldCreateCycleAsync(taskId, dependsOnTaskId))
            throw new InvalidOperationException(
                $"Adding dependency '{taskId}' → '{dependsOnTaskId}' would create a cycle.");

        var entity = new TaskDependencyEntity
        {
            TaskId = taskId,
            DependsOnTaskId = dependsOnTaskId,
            CreatedAt = DateTime.UtcNow
        };
        _db.TaskDependencies.Add(entity);

        // Bump UpdatedAt on the dependent task
        task.UpdatedAt = DateTime.UtcNow;

        _activity.Publish(
            ActivityEventType.TaskStatusUpdated, task.RoomId, null, taskId,
            $"Dependency added: \"{Truncate(task.Title, 40)}\" now depends on \"{Truncate(depTask.Title, 40)}\"");

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE") == true)
        {
            throw new InvalidOperationException(
                $"Dependency already exists: '{taskId}' → '{dependsOnTaskId}'.");
        }

        _logger.LogInformation("Task dependency added: {TaskId} → {DependsOnTaskId}", taskId, dependsOnTaskId);
        return await GetDependencyInfoAsync(taskId);
    }

    /// <summary>
    /// Removes a dependency between two tasks.
    /// </summary>
    public async Task<TaskDependencyInfo> RemoveDependencyAsync(string taskId, string dependsOnTaskId)
    {
        var dep = await _db.TaskDependencies
            .FirstOrDefaultAsync(d => d.TaskId == taskId && d.DependsOnTaskId == dependsOnTaskId)
            ?? throw new InvalidOperationException(
                $"No dependency found: '{taskId}' → '{dependsOnTaskId}'.");

        var task = await _db.Tasks.FindAsync(taskId);
        var depTask = await _db.Tasks.FindAsync(dependsOnTaskId);

        _db.TaskDependencies.Remove(dep);
        if (task is not null)
            task.UpdatedAt = DateTime.UtcNow;

        _activity.Publish(
            ActivityEventType.TaskStatusUpdated, task?.RoomId, null, taskId,
            $"Dependency removed: \"{Truncate(task?.Title ?? taskId, 40)}\" no longer depends on \"{Truncate(depTask?.Title ?? dependsOnTaskId, 40)}\"");

        await _db.SaveChangesAsync();

        _logger.LogInformation("Task dependency removed: {TaskId} → {DependsOnTaskId}", taskId, dependsOnTaskId);
        return await GetDependencyInfoAsync(taskId);
    }

    /// <summary>
    /// Returns full dependency information for a task (what it depends on + what depends on it).
    /// </summary>
    public async Task<TaskDependencyInfo> GetDependencyInfoAsync(string taskId)
    {
        var dependsOn = await _db.TaskDependencies
            .Where(d => d.TaskId == taskId)
            .Join(_db.Tasks, d => d.DependsOnTaskId, t => t.Id,
                (d, t) => new { t.Id, t.Title, t.Status })
            .ToListAsync();

        var dependedOnBy = await _db.TaskDependencies
            .Where(d => d.DependsOnTaskId == taskId)
            .Join(_db.Tasks, d => d.TaskId, t => t.Id,
                (d, t) => new { t.Id, t.Title, t.Status })
            .ToListAsync();

        return new TaskDependencyInfo(
            TaskId: taskId,
            DependsOn: dependsOn.Select(d => new TaskDependencySummary(
                TaskId: d.Id,
                Title: d.Title,
                Status: Enum.TryParse<Shared.Models.TaskStatus>(d.Status, out var s) ? s : Shared.Models.TaskStatus.Active,
                IsSatisfied: d.Status == nameof(Shared.Models.TaskStatus.Completed)
            )).ToList(),
            DependedOnBy: dependedOnBy.Select(d => new TaskDependencySummary(
                TaskId: d.Id,
                Title: d.Title,
                Status: Enum.TryParse<Shared.Models.TaskStatus>(d.Status, out var s2) ? s2 : Shared.Models.TaskStatus.Active,
                IsSatisfied: d.Status == nameof(Shared.Models.TaskStatus.Completed)
            )).ToList()
        );
    }

    /// <summary>
    /// Returns true if the task has any dependency that is not yet Completed.
    /// </summary>
    public async Task<bool> HasUnmetDependenciesAsync(string taskId)
    {
        return await _db.TaskDependencies
            .Where(d => d.TaskId == taskId)
            .Join(_db.Tasks, d => d.DependsOnTaskId, t => t.Id, (d, t) => t.Status)
            .AnyAsync(status => status != nameof(Shared.Models.TaskStatus.Completed));
    }

    /// <summary>
    /// Returns the IDs and titles of tasks blocking the given task.
    /// </summary>
    public async Task<List<TaskDependencySummary>> GetBlockingTasksAsync(string taskId)
    {
        var blockers = await _db.TaskDependencies
            .Where(d => d.TaskId == taskId)
            .Join(_db.Tasks, d => d.DependsOnTaskId, t => t.Id, (d, t) => t)
            .Where(t => t.Status != nameof(Shared.Models.TaskStatus.Completed))
            .Select(t => new { t.Id, t.Title, t.Status })
            .ToListAsync();

        return blockers.Select(t => new TaskDependencySummary(
            t.Id, t.Title,
            Enum.TryParse<Shared.Models.TaskStatus>(t.Status, out var s) ? s : Shared.Models.TaskStatus.Active,
            false
        )).ToList();
    }

    /// <summary>
    /// Loads dependency IDs for a batch of tasks (used by BuildTaskSnapshot).
    /// Returns a dict: taskId → (dependsOnIds, blockingIds).
    /// </summary>
    public async Task<Dictionary<string, (List<string> DependsOn, List<string> Blocking)>>
        GetBatchDependencyIdsAsync(IEnumerable<string> taskIds)
    {
        var ids = taskIds.ToList();
        if (ids.Count == 0) return new();

        var deps = await _db.TaskDependencies
            .Where(d => ids.Contains(d.TaskId))
            .Select(d => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync();

        var depTargetIds = deps.Select(d => d.DependsOnTaskId).Distinct().ToList();
        var targetStatuses = await _db.Tasks
            .Where(t => depTargetIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Status })
            .ToDictionaryAsync(t => t.Id, t => t.Status);

        var result = new Dictionary<string, (List<string> DependsOn, List<string> Blocking)>();
        foreach (var taskId in ids)
        {
            var taskDeps = deps.Where(d => d.TaskId == taskId).ToList();
            var dependsOn = taskDeps.Select(d => d.DependsOnTaskId).ToList();
            var blocking = taskDeps
                .Where(d => targetStatuses.TryGetValue(d.DependsOnTaskId, out var status)
                    && status != nameof(Shared.Models.TaskStatus.Completed))
                .Select(d => d.DependsOnTaskId)
                .ToList();
            result[taskId] = (dependsOn, blocking);
        }
        return result;
    }

    /// <summary>
    /// Returns downstream tasks that would become fully unblocked if the given task
    /// were marked as Completed. Treats completedTaskId as already satisfied so
    /// it can be called before SaveChangesAsync in the same unit of work.
    /// Only returns non-terminal tasks (excludes Completed and Cancelled).
    /// </summary>
    public async Task<List<(string TaskId, string Title, string? RoomId)>> GetTasksUnblockedByCompletionAsync(
        string completedTaskId)
    {
        // Step 1: find downstream task IDs (those that depend on the completing task)
        var downstreamTaskIds = await _db.TaskDependencies
            .Where(d => d.DependsOnTaskId == completedTaskId)
            .Select(d => d.TaskId)
            .ToListAsync();

        if (downstreamTaskIds.Count == 0)
            return [];

        // Step 2: for each downstream task, check if it has any OTHER unmet dependencies
        var result = new List<(string TaskId, string Title, string? RoomId)>();
        foreach (var taskId in downstreamTaskIds)
        {
            var task = await _db.Tasks.FindAsync(taskId);
            if (task is null) continue;

            // Skip terminal tasks — they can't be "unblocked"
            if (task.Status is nameof(Shared.Models.TaskStatus.Completed)
                             or nameof(Shared.Models.TaskStatus.Cancelled))
                continue;

            // Check for other unmet dependencies (excluding the completing task)
            var hasOtherBlockers = await _db.TaskDependencies
                .Where(d => d.TaskId == taskId && d.DependsOnTaskId != completedTaskId)
                .Join(_db.Tasks, d => d.DependsOnTaskId, t => t.Id, (d, t) => t.Status)
                .AnyAsync(status => status != nameof(Shared.Models.TaskStatus.Completed));

            if (!hasOtherBlockers)
                result.Add((task.Id, task.Title, task.RoomId));
        }

        return result;
    }

    // ── Cycle Detection ─────────────────────────────────────────

    /// <summary>
    /// BFS from dependsOnTaskId following existing dependency edges.
    /// If we reach taskId, adding the edge would create a cycle.
    /// </summary>
    private async Task<bool> WouldCreateCycleAsync(string taskId, string dependsOnTaskId)
    {
        // Load all dependency edges for BFS (small dataset — typically dozens of tasks)
        var allEdges = await _db.TaskDependencies
            .Select(d => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync();

        // Build adjacency: for each node, which nodes does it depend on?
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in allEdges)
        {
            if (!adjacency.TryGetValue(edge.TaskId, out var list))
            {
                list = [];
                adjacency[edge.TaskId] = list;
            }
            list.Add(edge.DependsOnTaskId);
        }

        // The proposed edge is: taskId → dependsOnTaskId
        // A cycle exists if dependsOnTaskId can already reach taskId via existing edges
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(dependsOnTaskId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (string.Equals(current, taskId, StringComparison.Ordinal))
                return true;

            if (!visited.Add(current))
                continue;

            if (adjacency.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                    queue.Enqueue(neighbor);
            }
        }

        return false;
    }

    private static string Truncate(string? value, int maxLength) =>
        value is null ? "" : value.Length <= maxLength ? value : value[..maxLength] + "…";
}
