using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages agent presence tracking: querying locations, moving agents between rooms,
/// and mapping between entities and domain models.
/// </summary>
public sealed class AgentLocationService : IAgentLocationService
{
    private readonly AgentAcademyDbContext _db;
    private readonly IAgentCatalog _catalog;
    private readonly ActivityPublisher _activity;

    public AgentLocationService(
        AgentAcademyDbContext db,
        IAgentCatalog catalog,
        ActivityPublisher activity)
    {
        _db = db;
        _catalog = catalog;
        _activity = activity;
    }

    /// <summary>
    /// Returns all agent locations.
    /// </summary>
    public async Task<List<AgentLocation>> GetAgentLocationsAsync()
    {
        var entities = await _db.AgentLocations.ToListAsync();
        return entities.Select(BuildAgentLocation).ToList();
    }

    /// <summary>
    /// Returns a single agent's location, or null if not tracked.
    /// </summary>
    public async Task<AgentLocation?> GetAgentLocationAsync(string agentId)
    {
        var entity = await _db.AgentLocations.FindAsync(agentId);
        return entity is null ? null : BuildAgentLocation(entity);
    }

    /// <summary>
    /// Moves an agent to a new room/state.
    /// </summary>
    public async Task<AgentLocation> MoveAgentAsync(
        string agentId, string roomId, AgentState state, string? breakoutRoomId = null)
    {
        var inCatalog = _catalog.Agents.Any(a => a.Id == agentId);
        if (!inCatalog)
        {
            var customConfig = await _db.AgentConfigs.FindAsync(agentId);
            if (customConfig is null)
                throw new InvalidOperationException($"Agent '{agentId}' not found in catalog or custom agents");
        }

        var now = DateTime.UtcNow;
        var entity = await _db.AgentLocations.FindAsync(agentId);

        if (entity is null)
        {
            entity = new AgentLocationEntity
            {
                AgentId = agentId,
                RoomId = roomId,
                State = state.ToString(),
                BreakoutRoomId = breakoutRoomId,
                UpdatedAt = now
            };
            _db.AgentLocations.Add(entity);
        }
        else
        {
            entity.RoomId = roomId;
            entity.State = state.ToString();
            entity.BreakoutRoomId = breakoutRoomId;
            entity.UpdatedAt = now;
        }

        _activity.Publish(ActivityEventType.PresenceUpdated, roomId, agentId, null,
            $"Agent {agentId} moved to {roomId} ({state})");

        await _db.SaveChangesAsync();

        return BuildAgentLocation(entity);
    }

    internal static AgentLocation BuildAgentLocation(AgentLocationEntity entity)
    {
        return new AgentLocation(
            AgentId: entity.AgentId,
            RoomId: entity.RoomId,
            State: Enum.Parse<AgentState>(entity.State),
            BreakoutRoomId: entity.BreakoutRoomId,
            UpdatedAt: entity.UpdatedAt
        );
    }
}
