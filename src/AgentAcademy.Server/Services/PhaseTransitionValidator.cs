using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Validates phase transition prerequisites for collaboration rooms.
/// Returns per-phase gate status and blocks transitions that violate prerequisites.
/// </summary>
public sealed class PhaseTransitionValidator : IPhaseTransitionValidator
{
    private static readonly CollaborationPhase[] PhaseOrder =
    [
        CollaborationPhase.Intake,
        CollaborationPhase.Planning,
        CollaborationPhase.Discussion,
        CollaborationPhase.Validation,
        CollaborationPhase.Implementation,
        CollaborationPhase.FinalSynthesis,
    ];

    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal)
    {
        nameof(Shared.Models.TaskStatus.Completed),
        nameof(Shared.Models.TaskStatus.Cancelled),
    };

    private static readonly HashSet<string> ApprovedOrCompletedStatuses = new(StringComparer.Ordinal)
    {
        nameof(Shared.Models.TaskStatus.Approved),
        nameof(Shared.Models.TaskStatus.Merging),
        nameof(Shared.Models.TaskStatus.Completed),
    };

    private readonly AgentAcademyDbContext _db;

    public PhaseTransitionValidator(AgentAcademyDbContext db) => _db = db;

    /// <summary>
    /// Computes prerequisite gates for all phases in the given room.
    /// </summary>
    public async Task<PhasePrerequisiteStatus> GetGatesAsync(string roomId)
    {
        var stats = await GetRoomTaskStatsAsync(roomId);
        var gates = new Dictionary<string, PhaseGate>();

        foreach (var phase in PhaseOrder)
        {
            gates[phase.ToString()] = EvaluateGate(phase, stats);
        }

        return new PhasePrerequisiteStatus(gates);
    }

    /// <summary>
    /// Validates whether a transition from <paramref name="currentPhase"/> to
    /// <paramref name="targetPhase"/> is allowed. Returns the gate for the target phase.
    /// </summary>
    public async Task<PhaseGate> ValidateTransitionAsync(
        string roomId, CollaborationPhase currentPhase, CollaborationPhase targetPhase)
    {
        if (currentPhase == targetPhase)
            return new PhaseGate(true);

        // Backward transitions are always allowed (rollback scenario)
        if (PhaseIndex(targetPhase) < PhaseIndex(currentPhase))
            return new PhaseGate(true);

        var stats = await GetRoomTaskStatsAsync(roomId);
        return EvaluateGate(targetPhase, stats);
    }

    private static PhaseGate EvaluateGate(CollaborationPhase phase, RoomTaskStats stats)
    {
        return phase switch
        {
            // Intake and Planning are always open
            CollaborationPhase.Intake => new PhaseGate(true),
            CollaborationPhase.Planning => new PhaseGate(true),

            // Discussion requires at least 1 task
            CollaborationPhase.Discussion => stats.TotalTasks > 0
                ? new PhaseGate(true)
                : new PhaseGate(false, "Create at least one task before entering Discussion."),

            // Validation requires at least 1 task
            CollaborationPhase.Validation => stats.TotalTasks > 0
                ? new PhaseGate(true)
                : new PhaseGate(false, "Create at least one task before entering Validation."),

            // Implementation requires at least 1 task in approved/completed state
            CollaborationPhase.Implementation => stats.ApprovedOrCompletedTasks > 0
                ? new PhaseGate(true)
                : new PhaseGate(false, $"At least one task must be Approved or Completed to enter Implementation. ({stats.TotalTasks} tasks, {stats.ApprovedOrCompletedTasks} approved/completed)"),

            // FinalSynthesis requires all tasks terminal (completed or cancelled)
            CollaborationPhase.FinalSynthesis => stats.TotalTasks > 0 && stats.NonTerminalTasks == 0
                ? new PhaseGate(true)
                : stats.TotalTasks == 0
                    ? new PhaseGate(false, "Create at least one task before entering Final Synthesis.")
                    : new PhaseGate(false, $"{stats.NonTerminalTasks} task(s) still in progress. All tasks must be Completed or Cancelled to enter Final Synthesis."),

            _ => new PhaseGate(true),
        };
    }

    private async Task<RoomTaskStats> GetRoomTaskStatsAsync(string roomId)
    {
        var tasks = await _db.Tasks
            .Where(t => t.RoomId == roomId)
            .Select(t => t.Status)
            .ToListAsync();

        return new RoomTaskStats(
            TotalTasks: tasks.Count,
            ApprovedOrCompletedTasks: tasks.Count(s => ApprovedOrCompletedStatuses.Contains(s)),
            NonTerminalTasks: tasks.Count(s => !TerminalStatuses.Contains(s))
        );
    }

    private static int PhaseIndex(CollaborationPhase phase)
        => Array.IndexOf(PhaseOrder, phase);

    private sealed record RoomTaskStats(
        int TotalTasks,
        int ApprovedOrCompletedTasks,
        int NonTerminalTasks
    );
}
