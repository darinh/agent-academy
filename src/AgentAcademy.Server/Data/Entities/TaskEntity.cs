namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a collaboration task.
/// Maps to the "tasks" table.
/// </summary>
public class TaskEntity
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SuccessCriteria { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string CurrentPhase { get; set; } = "Planning";
    public string CurrentPlan { get; set; } = string.Empty;
    public string ValidationStatus { get; set; } = "NotStarted";
    public string ValidationSummary { get; set; } = string.Empty;
    public string ImplementationStatus { get; set; } = "NotStarted";
    public string ImplementationSummary { get; set; } = string.Empty;
    public string PreferredRoles { get; set; } = "[]";
    public string? RoomId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public RoomEntity? Room { get; set; }
}
