using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages room/breakout plan content: get, set (create/update), and delete.
/// </summary>
public sealed class PlanService
{
    private readonly AgentAcademyDbContext _db;

    public PlanService(AgentAcademyDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the plan content for a room, or null if none exists.
    /// </summary>
    public async Task<PlanContent?> GetPlanAsync(string roomId)
    {
        var entity = await _db.Plans.FindAsync(roomId);
        return entity is null ? null : new PlanContent(entity.Content);
    }

    /// <summary>
    /// Creates or updates the plan for a room.
    /// </summary>
    public async Task SetPlanAsync(string roomId, string content)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new ArgumentException("Room ID is required.", nameof(roomId));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Plan content is required.", nameof(content));

        if (!await PlanTargetExistsAsync(roomId))
            throw new InvalidOperationException($"Room or breakout room '{roomId}' not found");

        var entity = await _db.Plans.FindAsync(roomId);
        var now = DateTime.UtcNow;

        if (entity is null)
        {
            _db.Plans.Add(new PlanEntity
            {
                RoomId = roomId,
                Content = content,
                UpdatedAt = now
            });
        }
        else
        {
            entity.Content = content;
            entity.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Deletes the plan for a room. Returns true if a plan was deleted.
    /// </summary>
    public async Task<bool> DeletePlanAsync(string roomId)
    {
        var entity = await _db.Plans.FindAsync(roomId);
        if (entity is null) return false;

        _db.Plans.Remove(entity);
        await _db.SaveChangesAsync();
        return true;
    }

    private async Task<bool> PlanTargetExistsAsync(string roomId)
    {
        return await _db.Rooms.AnyAsync(r => r.Id == roomId)
            || await _db.BreakoutRooms.AnyAsync(br => br.Id == roomId);
    }
}
