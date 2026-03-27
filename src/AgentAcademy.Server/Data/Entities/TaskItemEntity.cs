namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for an individual task work item.
/// Maps to the "task_items" table.
/// </summary>
public class TaskItemEntity
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string AssignedTo { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string? BreakoutRoomId { get; set; }
    public string? Evidence { get; set; }
    public string? Feedback { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
