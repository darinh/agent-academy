namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a sprint stage deliverable (requirements doc, plan, report, etc.).
/// Maps to the "sprint_artifacts" table.
/// </summary>
public class SprintArtifactEntity
{
    public int Id { get; set; }
    public string SprintId { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty; // Intake | Planning | Discussion | Validation | Implementation | FinalSynthesis
    public string Type { get; set; } = string.Empty; // RequirementsDocument | SprintPlan | ValidationReport | SprintReport | OverflowRequirements
    public string Content { get; set; } = string.Empty; // JSON
    public string? CreatedByAgentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public SprintEntity? Sprint { get; set; }
}
