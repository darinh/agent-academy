namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Represents a dependency between two tasks: TaskId depends on DependsOnTaskId.
/// Forms a DAG — cycles are rejected at the service layer.
/// </summary>
public class TaskDependencyEntity
{
    public string TaskId { get; set; } = string.Empty;
    public string DependsOnTaskId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public TaskEntity? Task { get; set; }
    public TaskEntity? DependsOn { get; set; }
}
