using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Manages breakout-room lifecycle execution and shutdown signaling.
/// </summary>
public interface IBreakoutLifecycleService
{
    /// <summary>
    /// Signals breakout processing to stop.
    /// </summary>
    void Stop();

    /// <summary>
    /// Runs the breakout lifecycle for an assigned agent and task.
    /// </summary>
    Task RunBreakoutLifecycleAsync(
        string breakoutRoomId,
        string agentId,
        string parentRoomId,
        AgentDefinition agent,
        string? taskBranch = null,
        string? worktreePath = null);
}
