using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages workspace–room relationships: creating default rooms for workspaces,
/// resolving startup room IDs, retiring legacy rooms, and moving agents.
/// Extracted from <see cref="RoomService"/> to isolate workspace orchestration
/// from room CRUD and queries.
/// </summary>
public sealed class WorkspaceRoomService : IWorkspaceRoomService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<WorkspaceRoomService> _logger;
    private readonly IAgentCatalog _catalog;
    private readonly IActivityPublisher _activity;

    public WorkspaceRoomService(
        AgentAcademyDbContext db,
        ILogger<WorkspaceRoomService> logger,
        IAgentCatalog catalog,
        IActivityPublisher activity)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activity = activity;
    }

    /// <summary>
    /// Ensures a default room exists for the given workspace.
    /// Prefers adopting the legacy "main" room (preserving its messages and task
    /// associations) over creating a duplicate workspace-scoped room.
    /// Moves all agents to the workspace's default room. Returns the default room ID.
    /// </summary>
    public async Task<string> EnsureDefaultRoomForWorkspaceAsync(string workspacePath)
    {
        // Phase 1: Adopt orphaned legacy room if available.
        // The legacy "main" room may contain messages and tasks from before
        // the workspace was activated. Adopting it keeps those visible.
        var adopted = await TryAdoptLegacyRoomAsync(workspacePath);
        if (adopted is not null)
        {
            await MoveAllAgentsToRoomAsync(adopted);
            return adopted;
        }

        // Phase 2: Check if workspace already has a room (including an adopted "main").
        // Prefer workspace-specific rooms over the legacy default to avoid conflicts
        // when both exist (e.g., after a partial repair).
        // Exclude archived AND completed rooms — they should not be resurrected as
        // the workspace default. After a sprint completes its room is frozen, and
        // the next sprint must get a fresh main room.
        var existingForWorkspace = await _db.Rooms
            .Where(r => r.WorkspacePath == workspacePath
                 && r.Status != nameof(RoomStatus.Archived)
                 && r.Status != nameof(RoomStatus.Completed)
                 && (r.Name.EndsWith("Main Room") || r.Name.EndsWith("Collaboration Room")))
            .OrderBy(r => r.Id == _catalog.DefaultRoomId ? 1 : 0)
            .FirstOrDefaultAsync();

        if (existingForWorkspace is not null)
        {
            var defaultRoomId = existingForWorkspace.Id;

            if (existingForWorkspace.Name != _catalog.DefaultRoomName)
            {
                existingForWorkspace.Name = _catalog.DefaultRoomName;
                existingForWorkspace.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Updated default room name to '{RoomName}'", _catalog.DefaultRoomName);
            }

            await RetireLegacyDefaultRoomAsync(workspacePath, defaultRoomId);
            await MoveAllAgentsToRoomAsync(defaultRoomId);
            return defaultRoomId;
        }

        var slug = RoomService.Normalize(Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar)));
        if (string.IsNullOrEmpty(slug)) slug = "project";
        var candidateId = $"{slug}-main";

        var collision = await _db.Rooms.FindAsync(candidateId);
        if (collision is not null)
        {
            // Two cases for collision:
            //   (a) Another workspace already adopted "{slug}-main" — disambiguate
            //       with a workspace-derived hash so the same workspace replaying
            //       this code path always picks the same ID.
            //   (b) THIS workspace already has a "{slug}-main" but it's terminal
            //       (Completed/Archived) — the next sprint needs a fresh main.
            //       Use a Guid-suffixed ID so we don't collide with the historical
            //       row.  Idempotency is maintained because the lookup above
            //       (`existingForWorkspace`) returns any non-terminal main first
            //       and we never get here.
            string suffix;
            if (collision.WorkspacePath != workspacePath)
            {
                suffix = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(workspacePath)))[..8].ToLowerInvariant();
            }
            else
            {
                suffix = Guid.NewGuid().ToString("N")[..8];
            }
            candidateId = $"{slug}-{suffix}-main";

            // Defensive: re-check the new candidate. Guid suffix should be unique
            // but a workspace-hash suffix could re-collide if another workspace
            // with the same slug+hash exists (impossibly unlikely, but cheap to
            // verify).
            if (await _db.Rooms.FindAsync(candidateId) is not null)
                candidateId = $"{slug}-{Guid.NewGuid():N}-main"[..Math.Min(64, slug.Length + 38)];
        }

        var now = DateTime.UtcNow;

        var room = new RoomEntity
        {
            Id = candidateId,
            Name = _catalog.DefaultRoomName,
            Status = nameof(RoomStatus.Idle),
            CurrentPhase = nameof(CollaborationPhase.Intake),
            WorkspacePath = workspacePath,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Rooms.Add(room);

        var workspace = await _db.Workspaces.FindAsync(workspacePath);
        var projectLabel = workspace?.ProjectName ?? slug;

        var welcomeMsg = new MessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RoomId = candidateId,
            SenderId = "system",
            SenderName = "System",
            SenderKind = nameof(MessageSenderKind.System),
            Kind = nameof(MessageKind.System),
            Content = $"Project loaded: {projectLabel}. Agents are ready.",
            SentAt = now
        };
        _db.Messages.Add(welcomeMsg);

        await _db.SaveChangesAsync();

        await RetireLegacyDefaultRoomAsync(workspacePath, candidateId);

        Publish(ActivityEventType.RoomCreated, candidateId, null, null,
            $"Default room created for workspace: {projectLabel}");

        _logger.LogInformation("Created default room '{RoomId}' for workspace '{Workspace}'",
            candidateId, workspacePath);

        await MoveAllAgentsToRoomAsync(candidateId);
        return candidateId;
    }

    /// <summary>
    /// Tries to adopt the orphaned legacy default room for the given workspace.
    /// If the legacy room exists with no workspace path and is not archived, claim it.
    /// If a duplicate workspace-scoped room was already created, archive the duplicate
    /// and adopt the legacy room (which holds the real conversation history).
    /// Returns the adopted room ID, or null if adoption was not possible.
    /// </summary>
    internal async Task<string?> TryAdoptLegacyRoomAsync(string workspacePath)
    {
        var legacyRoom = await _db.Rooms.FindAsync(_catalog.DefaultRoomId);
        if (legacyRoom is null
            || legacyRoom.WorkspacePath is not null
            || legacyRoom.Status == nameof(RoomStatus.Archived))
        {
            return null;
        }

        // Archive any non-archived duplicate workspace-scoped room that was created before adoption
        var duplicateRoom = await _db.Rooms.FirstOrDefaultAsync(
            r => r.WorkspacePath == workspacePath
                && r.Id != _catalog.DefaultRoomId
                && r.Status != nameof(RoomStatus.Archived)
                && (r.Name.EndsWith("Main Room") || r.Name.EndsWith("Collaboration Room")));

        if (duplicateRoom is not null)
        {
            // Archive but keep WorkspacePath so any tasks/messages in the duplicate
            // remain visible to workspace-scoped queries
            duplicateRoom.Status = nameof(RoomStatus.Archived);
            duplicateRoom.UpdatedAt = DateTime.UtcNow;
            _logger.LogInformation(
                "Archived duplicate workspace room '{RoomId}' in favor of adopted legacy room",
                duplicateRoom.Id);
        }

        legacyRoom.WorkspacePath = workspacePath;
        legacyRoom.Name = _catalog.DefaultRoomName;
        legacyRoom.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Adopted orphaned default room '{RoomId}' for workspace '{Workspace}'",
            legacyRoom.Id, workspacePath);

        return legacyRoom.Id;
    }

    /// <summary>
    /// If the legacy catalog default room was backfilled into this workspace,
    /// clear its WorkspacePath so it stops appearing alongside the real workspace default.
    /// </summary>
    internal async Task RetireLegacyDefaultRoomAsync(string workspacePath, string workspaceDefaultRoomId)
    {
        var legacyRoomId = _catalog.DefaultRoomId;
        if (legacyRoomId == workspaceDefaultRoomId) return;

        var legacyRoom = await _db.Rooms.FindAsync(legacyRoomId);
        if (legacyRoom is not null && legacyRoom.WorkspacePath == workspacePath)
        {
            legacyRoom.WorkspacePath = null;
            legacyRoom.Status = nameof(RoomStatus.Archived);
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "Retired legacy default room '{RoomId}' — archived and cleared WorkspacePath (was '{Workspace}')",
                legacyRoomId, workspacePath);
        }
    }

    /// <summary>
    /// Moves all configured agents to the specified room in Idle state.
    /// </summary>
    internal async Task MoveAllAgentsToRoomAsync(string roomId)
    {
        foreach (var agent in _catalog.Agents)
        {
            var loc = await _db.AgentLocations.FindAsync(agent.Id);
            if (loc is null)
            {
                _db.AgentLocations.Add(new AgentLocationEntity
                {
                    AgentId = agent.Id,
                    RoomId = roomId,
                    State = nameof(AgentState.Idle),
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                loc.RoomId = roomId;
                loc.BreakoutRoomId = null;
                loc.State = nameof(AgentState.Idle);
                loc.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Resolves the main room ID to use at startup for the given workspace.
    /// Triggers legacy room adoption if applicable.
    /// </summary>
    public async Task<string> ResolveStartupMainRoomIdAsync(string? activeWorkspace)
    {
        if (activeWorkspace is null)
        {
            return _catalog.DefaultRoomId;
        }

        // Attempt adoption first — ensures legacy room gets claimed on restart
        var adopted = await TryAdoptLegacyRoomAsync(activeWorkspace);
        if (adopted is not null)
        {
            return adopted;
        }

        var workspaceMainRoomId = await _db.Rooms
            .Where(r => r.WorkspacePath == activeWorkspace
                && (r.Name == _catalog.DefaultRoomName
                    || r.Name.EndsWith("Main Room")
                    || r.Name.EndsWith("Collaboration Room")))
            .OrderBy(r => r.Id == _catalog.DefaultRoomId ? 1 : 0)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        if (!string.IsNullOrWhiteSpace(workspaceMainRoomId))
        {
            return workspaceMainRoomId;
        }

        var legacyRoomExists = await _db.Rooms.AnyAsync(r => r.Id == _catalog.DefaultRoomId);
        if (legacyRoomExists)
        {
            return _catalog.DefaultRoomId;
        }

        return await EnsureDefaultRoomForWorkspaceAsync(activeWorkspace);
    }

    private ActivityEvent Publish(
        ActivityEventType type,
        string? roomId,
        string? actorId,
        string? taskId,
        string message,
        string? correlationId = null,
        ActivitySeverity severity = ActivitySeverity.Info)
        => _activity.Publish(type, roomId, actorId, taskId, message, correlationId, severity);
}
