namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a sprint — a numbered iteration of the build cycle.
/// Maps to the "sprints" table.
/// </summary>
public class SprintEntity
{
    public string Id { get; set; } = string.Empty;
    public int Number { get; set; }
    public string WorkspacePath { get; set; } = string.Empty;
    public string Status { get; set; } = "Active"; // Active | Completed | Cancelled
    public string CurrentStage { get; set; } = "Intake"; // Intake | Planning | Discussion | Validation | Implementation | FinalSynthesis
    public string? OverflowFromSprintId { get; set; }
    public bool AwaitingSignOff { get; set; }
    public string? PendingStage { get; set; }
    public DateTime? SignOffRequestedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    // Navigation
    public SprintEntity? OverflowFromSprint { get; set; }
}
