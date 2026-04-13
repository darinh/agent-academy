namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Junction entity linking a learning digest to the retrospective
/// comments it synthesized. Unique constraint on RetrospectiveCommentId
/// ensures each retrospective is digested exactly once.
/// Maps to the "learning_digest_sources" table.
/// </summary>
public class LearningDigestSourceEntity
{
    public int DigestId { get; set; }
    public string RetrospectiveCommentId { get; set; } = string.Empty;

    // Navigation
    public LearningDigestEntity? Digest { get; set; }
    public TaskCommentEntity? RetrospectiveComment { get; set; }
}
