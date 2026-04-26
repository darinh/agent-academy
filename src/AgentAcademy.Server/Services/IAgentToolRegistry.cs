using Microsoft.Extensions.AI;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Resolves SDK tool functions (<see cref="AIFunction"/>) for agents based on
/// their <c>EnabledTools</c> configuration. Tool groups map to sets of callable
/// functions that the Copilot SDK will make available to the LLM.
///
/// Read-only groups (task-state, code) return shared tool instances.
/// Write groups (task-write, memory) create per-agent tool instances that
/// capture the agent's identity for authorization.
/// </summary>
public interface IAgentToolRegistry
{
    /// <summary>
    /// Returns the list of <see cref="AIFunction"/> tools that should be registered
    /// for a given agent based on its <c>EnabledTools</c> groups.
    /// </summary>
    /// <param name="enabledTools">Tool group names from <c>AgentDefinition.EnabledTools</c>.</param>
    /// <param name="agentId">
    /// Agent ID, required when write groups (task-write, memory) are enabled.
    /// Used to scope write operations to the calling agent.
    /// </param>
    /// <param name="agentName">
    /// Agent display name, used in task notes and comments.
    /// </param>
    /// <param name="roomId">
    /// Room ID, used by write tools (artifact tracking) and the read tools
    /// when running inside a per-session worktree.
    /// </param>
    /// <param name="workspacePath">
    /// Optional worktree path for the calling session. When supplied, all
    /// per-session tools (read and write) operate inside this directory
    /// instead of the develop checkout.
    /// </param>
    /// <returns>A collection of AI functions to pass to <c>SessionConfig.Tools</c>.</returns>
    IReadOnlyList<AIFunction> GetToolsForAgent(
        IEnumerable<string> enabledTools,
        string? agentId = null,
        string? agentName = null,
        string? roomId = null,
        string? workspacePath = null);

    /// <summary>
    /// Returns all registered tool names for diagnostics.
    /// </summary>
    IReadOnlyList<string> GetAllToolNames();
}
