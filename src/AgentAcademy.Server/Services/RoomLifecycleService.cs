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
    /// Pure name-based heuristic for "looks like a main collaboration room".
    /// Used as the *first* filter when scanning workspace rooms; callers must
    /// additionally confirm by ID equality against the workspace's resolved
    /// main room (or the catalog default) before treating a row as the
    /// persistent main — otherwise a user-created room named e.g. "Security
    /// Collaboration Room" would be wrongly exempted from terminal-status
    /// flips. Workspace-agnostic.
    /// </summary>
    public static bool IsMainCollaborationRoomName(string roomName)
    {
        if (string.IsNullOrEmpty(roomName)) return false;
        return roomName.EndsWith("Main Room", StringComparison.Ordinal)
            || roomName.EndsWith("Collaboration Room", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the set of room IDs that represent the persistent main collaboration
    /// room for <paramref name="workspacePath"/>: at most one workspace-resolved
    /// main room ID plus the legacy catalog default room ID. Rooms in this set
    /// are exempt from sprint-completion terminal-status flips so they remain
    /// writable across sprint boundaries (B1).
    /// </summary>
    public async Task<HashSet<string>> GetExemptMainRoomIdsAsync(string workspacePath)
    {
        var exempt = new HashSet<string>(StringComparer.Ordinal) { _catalog.DefaultRoomId };
        if (!string.IsNullOrEmpty(workspacePath))
        {
            var resolved = await GetDefaultRoomForWorkspaceAsync(workspacePath);
            if (!string.IsNullOrEmpty(resolved)) exempt.Add(resolved);
        }
        return exempt;
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

    /// <summary>
    /// Marks every non-terminal room belonging to the given workspace as
    /// Completed in response to a sprint completing or being cancelled.
    /// Evacuates agents back to the workspace default room (including any
    /// agents currently in a breakout that descends from one of the workspace's
    /// rooms) and archives those breakout rooms. Idempotent.
    /// </summary>
    public async Task<int> MarkSprintRoomsCompletedAsync(string workspacePath, string? sprintId = null)
    {
        if (string.IsNullOrEmpty(workspacePath))
            return 0;

        var archivedStatus = nameof(RoomStatus.Archived);
        var completedStatus = nameof(RoomStatus.Completed);

        // Universe of non-archived rooms in this workspace, used to scope the
        // breakout sweep. We include already-Completed rooms because
        // SprintStageService.SyncWorkspaceRoomsToStageAsync flips rooms to
        // Completed when the sprint reaches FinalSynthesis — by the time
        // CompleteSprintAsync runs, parent rooms may already be terminal but
        // their breakouts can still be Active and need archiving here.
        var workspaceRoomIds = await _db.Rooms
            .Where(r => r.WorkspacePath == workspacePath && r.Status != archivedStatus)
            .Select(r => r.Id)
            .ToListAsync();

        if (workspaceRoomIds.Count == 0)
            return 0;

        var workspaceRoomIdSet = workspaceRoomIds.ToHashSet(StringComparer.Ordinal);

        // Subset that still needs status transition. Exempt the persistent main
        // collaboration room (B1): freezing it on sprint complete makes the
        // next sprint's kickoff filter (Status != Completed) skip it and
        // breaks the autonomy loop. Strict ID-based exemption (workspace's
        // resolved main + catalog DefaultRoomId) — does NOT exempt arbitrary
        // user-named rooms like "Security Collaboration Room". Breakouts and
        // other workspace rooms still freeze; agents still evacuate to the
        // main room below.
        var exemptIds = await GetExemptMainRoomIdsAsync(workspacePath);
        var roomsToTransition = (await _db.Rooms
            .Where(r => r.WorkspacePath == workspacePath
                && r.Status != archivedStatus
                && r.Status != completedStatus)
            .ToListAsync())
            .Where(r => !exemptIds.Contains(r.Id))
            .ToList();

        // Every active descendant breakout — regardless of whether its parent
        // room is still Active or already Completed.
        var breakoutRooms = await _db.BreakoutRooms
            .Where(b => workspaceRoomIdSet.Contains(b.ParentRoomId)
                && b.Status != archivedStatus
                && b.Status != completedStatus)
            .ToListAsync();

        if (roomsToTransition.Count == 0 && breakoutRooms.Count == 0)
            return 0;

        var breakoutRoomIds = breakoutRooms.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);

        // Lazy: only resolve the workspace default room if we actually have
        // agents to relocate. This avoids the unsupported EF translation of
        // EndsWith(string, StringComparison) running on workspaces with no
        // active participants (the common case at sprint completion).
        string? cachedDefaultRoomId = null;
        async Task<string> ResolveDefaultRoomAsync()
            => cachedDefaultRoomId ??= await GetDefaultRoomForWorkspaceAsync(workspacePath);

        var now = DateTime.UtcNow;
        var transitioned = 0;

        // Evacuate occupants of breakouts being archived even if their parent
        // is already Completed (and therefore not in roomsToTransition).
        if (breakoutRoomIds.Count > 0)
        {
            var breakoutOccupants = await _db.AgentLocations
                .Where(l => l.BreakoutRoomId != null
                    && breakoutRoomIds.Contains(l.BreakoutRoomId))
                .ToListAsync();

            if (breakoutOccupants.Count > 0)
            {
                var defaultRoomId = await ResolveDefaultRoomAsync();
                foreach (var location in breakoutOccupants)
                {
                    location.RoomId = defaultRoomId;
                    location.BreakoutRoomId = null;
                    location.State = nameof(AgentState.Idle);
                    location.UpdatedAt = now;

                    Publish(ActivityEventType.PresenceUpdated, location.RoomId, location.AgentId, null,
                        $"Agent {location.AgentId} evacuated from archived breakout (sprint completed)");
                }
            }
        }

        foreach (var room in roomsToTransition)
        {
            // Evacuate non-breakout participants — breakout occupants were
            // handled above and would otherwise be moved twice.
            var participants = await _db.AgentLocations
                .Where(l => l.RoomId == room.Id && l.BreakoutRoomId == null)
                .ToListAsync();

            if (participants.Count > 0)
            {
                var defaultRoomId = await ResolveDefaultRoomAsync();
                foreach (var location in participants)
                {
                    if (room.Id != defaultRoomId)
                        location.RoomId = defaultRoomId;
                    location.State = nameof(AgentState.Idle);
                    location.UpdatedAt = now;

                    Publish(ActivityEventType.PresenceUpdated, location.RoomId, location.AgentId, null,
                        $"Agent {location.AgentId} evacuated from completed sprint room");
                }
            }

            var oldStatus = room.Status;
            room.Status = completedStatus;
            room.UpdatedAt = now;
            transitioned++;

            Publish(ActivityEventType.RoomStatusChanged, room.Id, null, null,
                $"Room '{room.Name}' marked Completed: {oldStatus} → {completedStatus} (sprint completed)");
        }

        // Archive descendant breakouts. Marked Archived (not Completed) to
        // mirror the existing breakout close path in
        // BreakoutRoomService.CloseBreakoutRoomAsync.
        foreach (var breakout in breakoutRooms)
        {
            breakout.Status = archivedStatus;
            breakout.CloseReason = BreakoutRoomCloseReason.Cancelled.ToString();
            breakout.UpdatedAt = now;

            Publish(ActivityEventType.RoomClosed, breakout.ParentRoomId, breakout.AssignedAgentId, null,
                $"Breakout room '{breakout.Name}' archived (parent sprint completed)");
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Marked {Count} room(s) Completed and archived {BreakoutCount} breakout(s) for workspace '{Workspace}' (sprint {SprintId})",
            transitioned, breakoutRooms.Count, workspacePath, sprintId ?? "(unspecified)");

        return transitioned;
    }

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
        // EF Core's SQLite provider can't translate EndsWith(string, StringComparison.Ordinal),
        // so we use the parameterless EndsWith (translated to LIKE 'pattern'). Room names
        // like "Main Room" and "Collaboration Room" don't have case-ambiguous variants in
        // practice, so the loss of Ordinal semantics is immaterial here.
        // Deterministic ordering: prefer the exact DefaultRoomName match (the
        // canonical workspace main created by EnsureDefaultRoomForWorkspaceAsync),
        // then the legacy catalog DefaultRoomId, then by Id alphabetically.
        // Without this ordering, multiple suffix-matching rooms in the same
        // workspace pick non-deterministically (test flakiness; B1 exemption
        // could land on the wrong room).
        var workspaceDefaultRoom = await _db.Rooms
            .Where(r => r.WorkspacePath == workspacePath &&
                   (r.Name == _catalog.DefaultRoomName ||
                    r.Name.EndsWith("Main Room") ||
                    r.Name.EndsWith("Collaboration Room")))
            .OrderBy(r => r.Name == _catalog.DefaultRoomName ? 0 : 1)
            .ThenBy(r => r.Id == _catalog.DefaultRoomId ? 0 : 1)
            .ThenBy(r => r.Id)
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
