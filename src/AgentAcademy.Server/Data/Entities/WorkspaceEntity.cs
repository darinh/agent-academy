namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for onboarded workspace/project metadata.
/// Maps to the "workspaces" table. Uses Path as primary key.
/// </summary>
public class WorkspaceEntity
{
    public string Path { get; set; } = string.Empty;
    public string? ProjectName { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
