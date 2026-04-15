using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Handles breakout completion concerns and shared agent execution helpers
/// used by the breakout lifecycle loop.
/// </summary>
public interface IBreakoutCompletionService
{
    /// <summary>
    /// Signals completion/fix-loop processing to stop.
    /// </summary>
    void Stop();

    /// <summary>
    /// Executes an agent prompt and returns the raw response text.
    /// </summary>
    Task<string> RunAgentAsync(
        AgentDefinition agent,
        string prompt,
        string roomId,
        string? workspacePath = null);

    /// <summary>
    /// Parses and executes structured commands in an agent response.
    /// </summary>
    Task<CommandPipelineResult> ProcessCommandsAsync(
        AgentDefinition agent,
        string responseText,
        string roomId,
        string? workingDirectory = null);

    /// <summary>
    /// Runs post-loop breakout completion flow including presenting results,
    /// optional review cycle, and breakout finalization.
    /// </summary>
    Task HandleBreakoutCompleteAsync(
        IBreakoutRoomService breakoutRoomService,
        IMessageService messageService,
        ITaskItemService taskItemService,
        ITaskQueryService taskQueryService,
        IAgentLocationService agentLocationService,
        IRoomService roomService,
        IActivityPublisher activity,
        IAgentConfigService configService,
        string breakoutRoomId,
        string parentRoomId,
        string? worktreePath = null);
}
