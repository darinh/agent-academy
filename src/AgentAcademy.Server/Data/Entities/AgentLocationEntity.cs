namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for an agent's current location in the workspace.
/// Maps to the "agent_locations" table. Uses AgentId as primary key.
/// </summary>
public class AgentLocationEntity
{
    public string AgentId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string State { get; set; } = "Idle";
    public string? BreakoutRoomId { get; set; }
    public DateTime UpdatedAt { get; set; }
}
