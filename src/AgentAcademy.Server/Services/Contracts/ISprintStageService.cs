using AgentAcademy.Server.Data.Entities;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for sprint stage advancement, sign-off gating,
/// and approval/rejection operations.
/// </summary>
public interface ISprintStageService
{
    /// <summary>
    /// Advances the sprint to the next stage. Validates artifact gates,
    /// stage prerequisites, and sign-off gates.
    /// </summary>
    Task<SprintEntity> AdvanceStageAsync(string sprintId, bool force = false);

    /// <summary>
    /// Approves a pending sign-off and advances the sprint to the next stage.
    /// </summary>
    Task<SprintEntity> ApproveAdvanceAsync(string sprintId);

    /// <summary>
    /// Rejects a pending sign-off and returns the sprint to its current stage.
    /// </summary>
    Task<SprintEntity> RejectAdvanceAsync(string sprintId);

    /// <summary>
    /// Auto-advances a sprint whose sign-off window has expired.
    /// </summary>
    Task<SprintEntity> TimeOutSignOffAsync(string sprintId, CancellationToken ct = default);
}
