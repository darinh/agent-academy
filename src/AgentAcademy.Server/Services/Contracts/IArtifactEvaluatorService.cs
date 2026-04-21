using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for evaluating artifact files produced by agents against quality criteria.
/// </summary>
public interface IArtifactEvaluatorService
{
    /// <summary>
    /// Evaluates all artifact files for a room. Returns per-file results and an aggregate score.
    /// </summary>
    Task<(List<EvaluationResult> Artifacts, double AggregateScore)> EvaluateRoomArtifactsAsync(
        string roomId, CancellationToken ct = default);
}
