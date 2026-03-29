namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a collaboration room.
/// Maps to the "rooms" table.
/// </summary>
public class RoomEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Idle";
    public string CurrentPhase { get; set; } = "Intake";
    public string? WorkspacePath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public List<MessageEntity> Messages { get; set; } = [];
    public List<TaskEntity> Tasks { get; set; } = [];
    public List<BreakoutRoomEntity> BreakoutRooms { get; set; } = [];
    public List<ActivityEventEntity> ActivityEvents { get; set; } = [];
    public PlanEntity? Plan { get; set; }
}
