using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for validating room phase transitions and prerequisite gates.
/// </summary>
public interface IPhaseTransitionValidator
{
    Task<PhasePrerequisiteStatus> GetGatesAsync(string roomId);
    Task<PhaseGate> ValidateTransitionAsync(
        string roomId, CollaborationPhase currentPhase, CollaborationPhase targetPhase);
}
