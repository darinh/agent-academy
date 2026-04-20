using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Manages goal card lifecycle: creation, querying, and validated status transitions.
/// Goal card content is immutable after creation — only status can change.
/// </summary>
public interface IGoalCardService
{
    /// <summary>Creates a new goal card for an agent in a room.</summary>
    Task<GoalCard> CreateAsync(
        string agentId,
        string agentName,
        string roomId,
        CreateGoalCardRequest request,
        CancellationToken ct = default);

    /// <summary>Gets a goal card by ID.</summary>
    Task<GoalCard?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>Gets all active goal cards, optionally filtered by room.</summary>
    Task<List<GoalCard>> GetActiveAsync(string? roomId = null, CancellationToken ct = default);

    /// <summary>Gets goal cards with optional filters. Unlike GetActiveAsync, returns any status.</summary>
    Task<List<GoalCard>> QueryAsync(
        string? roomId = null,
        GoalCardStatus? status = null,
        GoalCardVerdict? verdict = null,
        CancellationToken ct = default);

    /// <summary>Gets goal cards for a specific agent.</summary>
    Task<List<GoalCard>> GetByAgentAsync(string agentId, CancellationToken ct = default);

    /// <summary>Gets goal cards for a specific task.</summary>
    Task<List<GoalCard>> GetByTaskAsync(string taskId, CancellationToken ct = default);

    /// <summary>Links an existing goal card to a task (for cards created before the task existed).</summary>
    Task<GoalCard?> AttachToTaskAsync(string goalCardId, string taskId, CancellationToken ct = default);

    /// <summary>
    /// Transitions a goal card to a new status. Validates the transition is legal.
    /// Returns null if the goal card doesn't exist.
    /// Throws InvalidOperationException for illegal transitions.
    /// </summary>
    Task<GoalCard?> UpdateStatusAsync(string id, GoalCardStatus newStatus, CancellationToken ct = default);
}
