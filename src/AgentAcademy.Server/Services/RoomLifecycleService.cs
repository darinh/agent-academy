using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles room lifecycle operations: closing, reopening, auto-archiving,
/// and stale-room cleanup. Extracted from <see cref="RoomService"/> to
/// separate lifecycle state-machine logic from CRUD and queries.
/// </summary>
public sealed class RoomLifecycleService : IRoomLifecycleService
{
    internal static readonly HashSet<string> TerminalTaskStatuses = new(StringComparer.Ordinal)
    {
        nameof(Shared.Models.TaskStatus.Completed),
        nameof(Shared.Models.TaskStatus.Cancelled),
    };

    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<RoomLifecycleService> _logger;
    private readonly IAgentCatalog _catalog;
    private readonly IActivityPublisher _activity;

    public RoomLifecycleService(
        AgentAcademyDbContext db,
        ILogger<RoomLifecycleService> logger,
        IAgentCatalog catalog,
        IActivityPublisher activity)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activity = activity;
    }

    /// <summary>
    /// Returns true when the given room is the active workspace's main collaboration room
    /// or the legacy catalog default room.
    /// </summary>
    public async Task<bool> IsMainCollaborationRoomAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId);
        if (room is null)
            return false;

        if (room.Id == _catalog.DefaultRoomId)
            return true;

        var activeWorkspace = await GetActiveWorkspacePathAsync();
        if (activeWorkspace is null || room.WorkspacePath != activeWorkspace)
            return false;

        return room.Name == _catalog.DefaultRoomName
            || room.Name.EndsWith("Main Room", StringComparison.Ordinal)
            || room.Name.EndsWith("Collaboration Room", StringComparison.Ordinal);
    }

    /// <summary>
    /// Archives a non-main collaboration room. Already archived rooms are a no-op.
    /// </summary>
    public async Task CloseRoomAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found.");

        if (await IsMainCollaborationRoomAsync(roomId))
            throw new InvalidOperationException($"Room '{room.Name}' is the main collaboration room and cannot be closed.");

        if (room.Status == nameof(RoomStatus.Archived))
            return;

        var participantCount = await _db.AgentLocations
            .Where(l => l.RoomId == roomId && l.BreakoutRoomId == null)
            .CountAsync();

        if (participantCount > 0)
            throw new InvalidOperationException($"Room '{room.Name}' has {participantCount} active participant(s) and cannot be closed.");

        room.Status = nameof(RoomStatus.Archived);
        room.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.RoomClosed, roomId, null, null,
            $"Room archived: {room.Name}");

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Reopens an archived room, restoring it to Idle status.
    /// </summary>
    public async Task ReopenRoomAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId)
            ?? throw new InvalidOperationException($"Room '{roomId}' not found.");

        if (room.Status != nameof(RoomStatus.Archived))
            throw new InvalidOperationException($"Room '{room.Name}' is not archived (current status: {room.Status}).");

        room.Status = nameof(RoomStatus.Idle);
        room.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.RoomStatusChanged, roomId, null, null,
            $"Room reopened: {room.Name}");

        await _db.SaveChangesAsync();

        _logger.LogInformation("Reopened room '{RoomId}' ({RoomName})", roomId, room.Name);
    }

    /// <summary>
    /// Auto-archives a room when all its tasks have reached a terminal state.
    /// Skips main collaboration rooms.
    /// </summary>
    public async Task TryAutoArchiveRoomAsync(string roomId)
    {
        if (await IsMainCollaborationRoomAsync(roomId))
            return;

        var room = await _db.Rooms.FindAsync(roomId);
        if (room is null || room.Status == nameof(RoomStatus.Archived))
            return;

        var hasNonTerminalTask = await _db.Tasks
            .Where(t => t.RoomId == roomId && !TerminalTaskStatuses.Contains(t.Status))
            .AnyAsync();

        if (hasNonTerminalTask)
            return;

        var hasAnyTask = await _db.Tasks.AnyAsync(t => t.RoomId == roomId);
        if (!hasAnyTask)
            return;

        await EvacuateRoomAsync(roomId);

        room.Status = nameof(RoomStatus.Archived);
        room.UpdatedAt = DateTime.UtcNow;

        Publish(ActivityEventType.RoomClosed, roomId, null, null,
            $"Room auto-archived (all tasks complete): {room.Name}");

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Auto-archived room '{RoomId}' ({RoomName}) — all tasks terminal",
            roomId, room.Name);
    }

    /// <summary>
    /// Scans for stale rooms (all tasks terminal) that are still Active/Completed
    /// and archives them. Returns the count of rooms cleaned up.
    /// </summary>
    public async Task<int> CleanupStaleRoomsAsync()
    {
        var candidateRooms = await _db.Rooms
            .Where(r => r.Status != nameof(RoomStatus.Archived))
            .ToListAsync();

        var cleanedCount = 0;
        foreach (var room in candidateRooms)
        {
            if (await IsMainCollaborationRoomAsync(room.Id))
                continue;

            var tasks = await _db.Tasks
                .Where(t => t.RoomId == room.Id)
                .Select(t => t.Status)
                .ToListAsync();

            if (tasks.Count == 0 || tasks.Any(s => !TerminalTaskStatuses.Contains(s)))
                continue;

            await EvacuateRoomAsync(room.Id);

            room.Status = nameof(RoomStatus.Archived);
            room.UpdatedAt = DateTime.UtcNow;

            Publish(ActivityEventType.RoomClosed, room.Id, null, null,
                $"Room cleaned up (stale): {room.Name}");

            cleanedCount++;
        }

        if (cleanedCount > 0)
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation("Cleaned up {Count} stale room(s)", cleanedCount);
        }

        return cleanedCount;
    }

    // ── Private Helpers ─────────────────────────────────────────

    private async Task<string?> GetActiveWorkspacePathAsync()
    {
        return await _db.Workspaces
            .Where(w => w.IsActive)
            .Select(w => w.Path)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Moves all agents currently in a room back to the appropriate default room.
    /// </summary>
    private async Task EvacuateRoomAsync(string roomId)
    {
        var room = await _db.Rooms.FindAsync(roomId);
        var defaultRoomId = room?.WorkspacePath is not null
            ? await GetDefaultRoomForWorkspaceAsync(room.WorkspacePath)
            : _catalog.DefaultRoomId;

        var agentsInRoom = await _db.AgentLocations
            .Where(l => l.RoomId == roomId)
            .ToListAsync();

        foreach (var location in agentsInRoom)
        {
            location.RoomId = defaultRoomId;
            location.State = nameof(AgentState.Idle);
            location.BreakoutRoomId = null;
            location.UpdatedAt = DateTime.UtcNow;

            Publish(ActivityEventType.PresenceUpdated, defaultRoomId, location.AgentId, null,
                $"Agent {location.AgentId} returned to default room (room archived)");
        }
    }

    private async Task<string> GetDefaultRoomForWorkspaceAsync(string workspacePath)
    {
        var workspaceDefaultRoom = await _db.Rooms
            .Where(r => r.WorkspacePath == workspacePath &&
                   (r.Name == _catalog.DefaultRoomName ||
                    r.Name.EndsWith("Main Room", StringComparison.Ordinal) ||
                    r.Name.EndsWith("Collaboration Room", StringComparison.Ordinal)))
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        return workspaceDefaultRoom ?? _catalog.DefaultRoomId;
    }

    private void Publish(
        ActivityEventType type,
        string? roomId,
        string? actorId,
        string? taskId,
        string message,
        string? correlationId = null,
        ActivitySeverity severity = ActivitySeverity.Info)
        => _activity.Publish(type, roomId, actorId, taskId, message, correlationId, severity);
}
