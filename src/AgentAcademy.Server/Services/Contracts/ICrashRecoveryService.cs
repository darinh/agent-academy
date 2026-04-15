namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Handles server-instance tracking and crash recovery operations.
/// </summary>
public interface ICrashRecoveryService
{
    /// <summary>
    /// Records the current server instance and marks unclean prior instances as crashed.
    /// </summary>
    Task RecordServerInstanceAsync();

    /// <summary>
    /// Repairs crash fallout (breakouts, agent state, and task assignment) for the main room.
    /// </summary>
    Task<CrashRecoveryResult> RecoverFromCrashAsync(string mainRoomId);
}
