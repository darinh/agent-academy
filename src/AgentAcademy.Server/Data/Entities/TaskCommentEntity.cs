namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a comment or finding attached to a task.
/// Maps to the "task_comments" table.
/// </summary>
public class TaskCommentEntity
{
    public string Id { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string CommentType { get; set; } = "Comment";
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public TaskEntity? Task { get; set; }
}
