using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Handles one-time startup initialization: recording the server instance,
/// ensuring the default room exists, seeding agent locations, and resolving
/// the startup main room.
/// </summary>
public sealed class InitializationService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<InitializationService> _logger;
    private readonly IAgentCatalog _catalog;
    private readonly IActivityPublisher _activity;
    private readonly ICrashRecoveryService _crashRecovery;
    private readonly IRoomService _rooms;
    private readonly IWorkspaceRoomService _workspaceRooms;

    public InitializationService(
        AgentAcademyDbContext db,
        ILogger<InitializationService> logger,
        IAgentCatalog catalog,
        IActivityPublisher activity,
        ICrashRecoveryService crashRecovery,
        IRoomService rooms,
        IWorkspaceRoomService workspaceRooms)
    {
        _db = db;
        _logger = logger;
        _catalog = catalog;
        _activity = activity;
        _crashRecovery = crashRecovery;
        _rooms = rooms;
        _workspaceRooms = workspaceRooms;
    }

    /// <summary>
    /// Ensures the default room and agent locations exist.
    /// Call once at startup within a scope.
    /// Also tracks server instance lifecycle for crash detection.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _crashRecovery.RecordServerInstanceAsync();

        var defaultRoomId = _catalog.DefaultRoomId;
        var activeWorkspace = await GetActiveWorkspacePathAsync();

        // When a workspace is active, EnsureDefaultRoomForWorkspaceAsync handles room creation.
        // Only create the legacy "main" room when no workspace exists (first boot).
        if (activeWorkspace is null)
        {
            var existing = await _db.Rooms.FindAsync(defaultRoomId);

            if (existing is null)
            {
                var now = DateTime.UtcNow;
                var room = new RoomEntity
                {
                    Id = defaultRoomId,
                    Name = _catalog.DefaultRoomName,
                    Status = nameof(RoomStatus.Idle),
                    CurrentPhase = nameof(CollaborationPhase.Intake),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _db.Rooms.Add(room);

                var welcomeMsg = new MessageEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    RoomId = defaultRoomId,
                    SenderId = "system",
                    SenderName = "System",
                    SenderKind = nameof(MessageSenderKind.System),
                    Kind = nameof(MessageKind.System),
                    Content = "Collaboration host started. Agents are loading.",
                    SentAt = now
                };
                _db.Messages.Add(welcomeMsg);

                await _db.SaveChangesAsync();

                _activity.Publish(ActivityEventType.RoomCreated, defaultRoomId, null, null,
                    $"Default room created: {_catalog.DefaultRoomName}");

                foreach (var agent in _catalog.Agents)
                {
                    _activity.Publish(ActivityEventType.AgentLoaded, defaultRoomId, agent.Id, null,
                        $"Agent loaded: {agent.Name} ({agent.Role})");
                }

                _logger.LogInformation("Created default room '{RoomName}' with {AgentCount} agents",
                    _catalog.DefaultRoomName, _catalog.Agents.Count);
            }
        }

        await _workspaceRooms.ResolveStartupMainRoomIdAsync(activeWorkspace);

        // Initialize agent locations for any agent not already tracked
        foreach (var agent in _catalog.Agents)
        {
            var loc = await _db.AgentLocations.FindAsync(agent.Id);
            if (loc is null)
            {
                _db.AgentLocations.Add(new AgentLocationEntity
                {
                    AgentId = agent.Id,
                    RoomId = defaultRoomId,
                    State = nameof(AgentState.Idle),
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task<string?> GetActiveWorkspacePathAsync()
    {
        var active = await _db.Workspaces
            .Where(w => w.IsActive)
            .Select(w => w.Path)
            .FirstOrDefaultAsync();
        return active;
    }
}
