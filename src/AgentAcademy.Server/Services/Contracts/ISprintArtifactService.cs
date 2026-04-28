using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;

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

    /// <summary>
    /// Returns the <c>OverallVerdict</c> of the most recent
    /// <c>SelfEvaluationReport</c> artifact for a sprint, or <c>null</c> if
    /// none has been stored or the latest one is unparseable. Shares the same
    /// query semantics as the Implementation→FinalSynthesis verdict gate in
    /// <c>SprintStageService.AdvanceStageAsync</c> (order by <c>CreatedAt</c>
    /// desc, tie-break by <c>Id</c> desc) so the terminal-stage driver and
    /// the gate cannot disagree about which report is "latest". See
    /// <c>specs/100-product-vision/sprint-terminal-stage-handler-design.md §6.1</c>.
    /// </summary>
    Task<SelfEvaluationOverallVerdict?> GetLatestSelfEvalVerdictAsync(
        string sprintId, CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if at least one artifact matching
    /// <paramref name="sprintId"/>, <paramref name="stage"/>, and
    /// <paramref name="type"/> is stored. Used by the terminal-stage driver
    /// to detect <c>SprintReport</c> at FinalSynthesis without loading the
    /// content. See
    /// <c>specs/100-product-vision/sprint-terminal-stage-handler-design.md §6.1</c>.
    /// </summary>
    Task<bool> HasArtifactAsync(
        string sprintId, string stage, string type, CancellationToken ct = default);
}
