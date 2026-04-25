using AgentAcademy.Server.Data.Entities;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for sprint artifact storage, retrieval, and validation.
/// </summary>
public interface ISprintArtifactService
{
    /// <summary>
    /// Stores a deliverable artifact for a sprint stage.
    /// If an artifact of the same type already exists for the stage, it is updated.
    /// </summary>
    Task<SprintArtifactEntity> StoreArtifactAsync(
        string sprintId, string stage, string type, string content, string? agentId = null);

    /// <summary>
    /// Returns artifacts for a sprint, optionally filtered by stage.
    /// </summary>
    Task<List<SprintArtifactEntity>> GetSprintArtifactsAsync(
        string sprintId, string? stage = null);

    /// <summary>
    /// Returns the most recent <c>SelfEvaluationReport</c> artifact for a sprint
    /// (any stage, ordered by <c>CreatedAt</c> desc), or <c>null</c> if none has
    /// been stored. Used by the API surface (P1.4 §6) and the verdict gate.
    /// </summary>
    Task<SprintArtifactEntity?> GetLatestSelfEvalReportAsync(string sprintId);
}
