namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Summary of actions performed during crash recovery.
/// </summary>
public sealed record CrashRecoveryResult(
    int ClosedBreakoutRooms,
    int ResetWorkingAgents,
    int ResetTasks);
