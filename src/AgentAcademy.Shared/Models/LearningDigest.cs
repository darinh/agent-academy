namespace AgentAcademy.Shared.Models;

/// <summary>
/// A learning digest — a periodic synthesis of multiple retrospective
/// summaries into cross-cutting shared knowledge.
/// </summary>
public record LearningDigest(
    int Id,
    DateTime CreatedAt,
    string Summary,
    int MemoriesCreated,
    int RetrospectivesProcessed
);
