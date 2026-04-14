using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Thrown when a phase transition is blocked by unmet prerequisites.
/// The controller maps this to a 409 Conflict response.
/// </summary>
public sealed class PhasePrerequisiteException : InvalidOperationException
{
    public CollaborationPhase TargetPhase { get; }
    public PhaseGate Gate { get; }

    public PhasePrerequisiteException(CollaborationPhase targetPhase, PhaseGate gate)
        : base(gate.Reason ?? $"Prerequisites not met for phase {targetPhase}.")
    {
        TargetPhase = targetPhase;
        Gate = gate;
    }
}
