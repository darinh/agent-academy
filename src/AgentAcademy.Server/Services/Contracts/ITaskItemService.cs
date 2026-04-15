using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// CRUD operations for breakout-level work items (<see cref="TaskItem"/>).
/// These are lightweight items within a breakout room, distinct from the
/// top-level <c>TaskEntity</c> managed by <see cref="ITaskLifecycleService"/>.
///
/// TaskItems use <see cref="TaskItemStatus"/> (Pending, Active, Done, Rejected),
/// not <see cref="TaskStatus"/> (the full lifecycle enum).
/// </summary>
public interface ITaskItemService
{
    /// <summary>
    /// Creates a new task item in a room/breakout.
    /// </summary>
    Task<TaskItem> CreateTaskItemAsync(
        string title,
        string description,
        string assignedTo,
        string roomId,
        string? breakoutRoomId);

    /// <summary>
    /// Updates the status of a task item, with optional evidence text.
    /// </summary>
    /// <exception cref="InvalidOperationException">Task item not found.</exception>
    Task UpdateTaskItemStatusAsync(string taskItemId, TaskItemStatus status, string? evidence = null);

    /// <summary>
    /// Returns task items associated with a breakout room.
    /// </summary>
    Task<List<TaskItem>> GetBreakoutTaskItemsAsync(string breakoutRoomId);

    /// <summary>
    /// Returns all task items that are not done (Pending or Active).
    /// </summary>
    Task<List<TaskItem>> GetActiveTaskItemsAsync();

    /// <summary>
    /// Returns a single task item by ID, or null if not found.
    /// </summary>
    Task<TaskItem?> GetTaskItemAsync(string taskItemId);

    /// <summary>
    /// Returns task items filtered by room and/or status.
    /// </summary>
    Task<List<TaskItem>> GetTaskItemsAsync(string? roomId = null, TaskItemStatus? status = null);
}
