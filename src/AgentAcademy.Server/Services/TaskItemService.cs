using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages task item CRUD — the individual work items within a breakout room.
/// </summary>
public sealed class TaskItemService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<TaskItemService> _logger;

    public TaskItemService(
        AgentAcademyDbContext db,
        ILogger<TaskItemService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new task item in a room/breakout.
    /// </summary>
    public async Task<TaskItem> CreateTaskItemAsync(
        string title, string description, string assignedTo,
        string roomId, string? breakoutRoomId)
    {
        var now = DateTime.UtcNow;
        var entity = new TaskItemEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Description = description,
            Status = nameof(TaskItemStatus.Pending),
            AssignedTo = assignedTo,
            RoomId = roomId,
            BreakoutRoomId = breakoutRoomId,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.TaskItems.Add(entity);
        await _db.SaveChangesAsync();

        return BuildTaskItem(entity);
    }

    /// <summary>
    /// Updates the status of a task item, with optional evidence.
    /// </summary>
    public async Task UpdateTaskItemStatusAsync(
        string taskItemId, TaskItemStatus status, string? evidence = null)
    {
        var entity = await _db.TaskItems.FindAsync(taskItemId);
        if (entity is null)
            throw new InvalidOperationException($"Task item '{taskItemId}' not found");

        entity.Status = status.ToString();
        entity.UpdatedAt = DateTime.UtcNow;
        if (evidence is not null) entity.Evidence = evidence;

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns task items associated with a breakout room.
    /// </summary>
    public async Task<List<TaskItem>> GetBreakoutTaskItemsAsync(string breakoutRoomId)
    {
        var entities = await _db.TaskItems
            .Where(t => t.BreakoutRoomId == breakoutRoomId)
            .ToListAsync();

        return entities.Select(BuildTaskItem).ToList();
    }

    /// <summary>
    /// Returns all task items that are not done (Pending or Active).
    /// </summary>
    public async Task<List<TaskItem>> GetActiveTaskItemsAsync()
    {
        var activeWorkspace = await GetActiveWorkspacePathAsync();
        var activeStatuses = new[] { nameof(TaskItemStatus.Pending), nameof(TaskItemStatus.Active) };
        var query = _db.TaskItems
            .Where(t => activeStatuses.Contains(t.Status));

        if (activeWorkspace is not null)
        {
            var workspaceRoomIds = await _db.Rooms
                .Where(r => r.WorkspacePath == activeWorkspace)
                .Select(r => r.Id)
                .ToListAsync();
            query = query.Where(t => workspaceRoomIds.Contains(t.RoomId));
        }

        var entities = await query.OrderBy(t => t.CreatedAt).ToListAsync();
        return entities.Select(BuildTaskItem).ToList();
    }

    /// <summary>
    /// Returns a single task item by ID, or null if not found.
    /// </summary>
    public async Task<TaskItem?> GetTaskItemAsync(string taskItemId)
    {
        var entity = await _db.TaskItems.FindAsync(taskItemId);
        return entity is null ? null : BuildTaskItem(entity);
    }

    /// <summary>
    /// Returns task items filtered by room and/or status.
    /// </summary>
    public async Task<List<TaskItem>> GetTaskItemsAsync(string? roomId = null, TaskItemStatus? status = null)
    {
        var query = _db.TaskItems.AsQueryable();

        if (roomId is not null)
            query = query.Where(t => t.RoomId == roomId || t.BreakoutRoomId == roomId);

        if (status is not null)
            query = query.Where(t => t.Status == status.Value.ToString());

        // Scope to active workspace when no room filter is specified
        if (roomId is null)
        {
            var activeWorkspace = await GetActiveWorkspacePathAsync();
            if (activeWorkspace is not null)
            {
                var workspaceRoomIds = await _db.Rooms
                    .Where(r => r.WorkspacePath == activeWorkspace)
                    .Select(r => r.Id)
                    .ToListAsync();
                query = query.Where(t => workspaceRoomIds.Contains(t.RoomId));
            }
        }

        var entities = await query.OrderBy(t => t.CreatedAt).ToListAsync();
        return entities.Select(BuildTaskItem).ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────

    internal async Task<string?> GetActiveWorkspacePathAsync()
    {
        return await _db.Workspaces
            .Where(w => w.IsActive)
            .Select(w => w.Path)
            .FirstOrDefaultAsync();
    }

    private static TaskItem BuildTaskItem(TaskItemEntity e) =>
        new(e.Id, e.Title, e.Description,
            Enum.Parse<TaskItemStatus>(e.Status),
            e.AssignedTo, e.RoomId, e.BreakoutRoomId,
            e.Evidence, e.Feedback,
            e.CreatedAt, e.UpdatedAt);
}
