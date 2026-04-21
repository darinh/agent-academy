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
}
