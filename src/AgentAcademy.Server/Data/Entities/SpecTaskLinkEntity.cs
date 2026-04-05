namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Junction table linking spec sections to tasks for traceability.
/// A task can reference multiple spec sections, and a spec section can be referenced by multiple tasks.
/// </summary>
public class SpecTaskLinkEntity
{
    public string Id { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string SpecSectionId { get; set; } = string.Empty;
    public string LinkType { get; set; } = "Implements";
    public string LinkedByAgentId { get; set; } = string.Empty;
    public string LinkedByAgentName { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public TaskEntity? Task { get; set; }
}
