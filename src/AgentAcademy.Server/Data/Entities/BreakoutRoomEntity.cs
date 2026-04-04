namespace AgentAcademy.Server.Data.Entities;

public enum BreakoutRoomCloseReason
{
    Completed,
    Recalled,
    Cancelled,
    StuckDetected,
    ClosedByRecovery,
    Failed
}

/// <summary>
/// Persistence entity for a breakout room assigned to a single agent.
/// Maps to the "breakout_rooms" table.
/// </summary>
public class BreakoutRoomEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ParentRoomId { get; set; } = string.Empty;
    public string AssignedAgentId { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? CloseReason { get; set; }
    public string? TaskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public RoomEntity ParentRoom { get; set; } = null!;
    public List<BreakoutMessageEntity> Messages { get; set; } = [];
}
