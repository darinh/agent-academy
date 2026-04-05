using Microsoft.Extensions.AI;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Resolves SDK tool functions (<see cref="AIFunction"/>) for agents based on
/// their <c>EnabledTools</c> configuration. Tool groups map to sets of callable
/// functions that the Copilot SDK will make available to the LLM.
/// </summary>
public interface IAgentToolRegistry
{
    /// <summary>
    /// Returns the list of <see cref="AIFunction"/> tools that should be registered
    /// for a given agent based on its <c>EnabledTools</c> groups.
    /// </summary>
    /// <param name="enabledTools">Tool group names from <c>AgentDefinition.EnabledTools</c>.</param>
    /// <returns>A collection of AI functions to pass to <c>SessionConfig.Tools</c>.</returns>
    IReadOnlyList<AIFunction> GetToolsForAgent(IEnumerable<string> enabledTools);

    /// <summary>
    /// Returns all registered tool names for diagnostics.
    /// </summary>
    IReadOnlyList<string> GetAllToolNames();
}
