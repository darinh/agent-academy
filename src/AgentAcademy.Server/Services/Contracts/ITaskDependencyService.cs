using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Manages the task dependency directed acyclic graph (DAG). Tasks can declare
/// dependencies on other tasks; a task with unmet dependencies cannot be
/// claimed or activated.
///
/// Invariants:
/// <list type="bullet">
///   <item>No self-dependencies (taskId ≠ dependsOnTaskId).</item>
///   <item>No cycles — adding a dependency that would create a cycle is rejected.</item>
///   <item>Cannot depend on a cancelled task.</item>
///   <item>Cannot add dependencies to a terminal task (Completed/Cancelled).</item>
///   <item>A dependency is "satisfied" when the depended-on task reaches Completed status.</item>
/// </list>
/// </summary>
public interface ITaskDependencyService
{
    /// <summary>
    /// Adds a dependency: <paramref name="taskId"/> depends on <paramref name="dependsOnTaskId"/>.
    /// Validates both tasks exist, prevents self-dependencies, rejects cycles (BFS),
    /// and rejects dependencies on cancelled tasks or from terminal tasks.
    /// </summary>
    /// <returns>Updated dependency info for <paramref name="taskId"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Either task not found, self-dependency, cycle detected, target is cancelled,
    /// or source task is terminal.
    /// </exception>
    Task<TaskDependencyInfo> AddDependencyAsync(string taskId, string dependsOnTaskId);

    /// <summary>
    /// Removes a dependency between two tasks.
    /// </summary>
    /// <returns>Updated dependency info for <paramref name="taskId"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Dependency not found.
    /// </exception>
    Task<TaskDependencyInfo> RemoveDependencyAsync(string taskId, string dependsOnTaskId);

    /// <summary>
    /// Returns full dependency information for a task: what it depends on
    /// and what depends on it, with title and satisfaction status.
    /// </summary>
    Task<TaskDependencyInfo> GetDependencyInfoAsync(string taskId);

    /// <summary>
    /// Returns true if the task has any dependency that is not yet Completed.
    /// </summary>
    Task<bool> HasUnmetDependenciesAsync(string taskId);

    /// <summary>
    /// Returns summary information for tasks that are blocking the given task
    /// (i.e., dependencies that are not yet Completed).
    /// </summary>
    Task<List<TaskDependencySummary>> GetBlockingTasksAsync(string taskId);

    /// <summary>
    /// Loads dependency IDs for a batch of tasks in a single query.
    /// Used by snapshot builders to populate <c>DependsOnTaskIds</c> and
    /// <c>BlockingTaskIds</c> without N+1 queries.
    /// </summary>
    Task<Dictionary<string, (List<string> DependsOn, List<string> Blocking)>> GetBatchDependencyIdsAsync(
        IEnumerable<string> taskIds);

    /// <summary>
    /// Returns downstream tasks that would become fully unblocked if the
    /// given task were marked as Completed. Used by
    /// <see cref="ITaskLifecycleService.CompleteTaskCoreAsync"/> to publish
    /// unblock notifications.
    /// </summary>
    Task<List<(string TaskId, string Title, string? RoomId)>> GetTasksUnblockedByCompletionAsync(
        string completedTaskId);
}
