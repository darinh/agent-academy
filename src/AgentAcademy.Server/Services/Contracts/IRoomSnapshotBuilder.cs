using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Builds read-model snapshots of rooms including messages, active task, and participants.
/// </summary>
public interface IRoomSnapshotBuilder
{
    /// <summary>
    /// Builds a full room snapshot including messages, active task, and participants.
    /// </summary>
    Task<RoomSnapshot> BuildRoomSnapshotAsync(
        RoomEntity room, List<AgentLocationEntity>? preloadedLocations = null);
}
