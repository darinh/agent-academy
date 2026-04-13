namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a learning digest — a periodic synthesis of
/// retrospective summaries into cross-cutting shared memories.
/// Maps to the "learning_digests" table.
/// </summary>
public class LearningDigestEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int MemoriesCreated { get; set; }
    public int RetrospectivesProcessed { get; set; }
    public string Status { get; set; } = "Pending";

    // Navigation
    public List<LearningDigestSourceEntity> Sources { get; set; } = new();
}
