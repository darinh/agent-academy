namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a room's collaboration plan.
/// Maps to the "plans" table. Uses RoomId as primary key (one plan per room).
/// </summary>
public class PlanEntity
{
    public string RoomId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }

    // Sprint association
    public string? SprintId { get; set; }

    // Navigation
    public SprintEntity? Sprint { get; set; }
}
