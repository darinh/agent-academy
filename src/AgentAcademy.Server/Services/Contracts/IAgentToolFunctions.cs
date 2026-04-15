using AgentAcademy.Shared.Models;
using Microsoft.Extensions.AI;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Factory for the AIFunction instances that agents call as tools.
/// Read-only tools (task-state, code) are agent-agnostic; write tools
/// (task-write, memory, code-write) capture the calling agent's identity.
/// </summary>
public interface IAgentToolFunctions
{
    /// <summary>
    /// Creates all AIFunction instances for the "task-state" tool group.
    /// </summary>
    IReadOnlyList<AIFunction> CreateTaskStateTools();

    /// <summary>
    /// Creates all AIFunction instances for the "code" tool group.
    /// </summary>
    IReadOnlyList<AIFunction> CreateCodeTools();

    /// <summary>
    /// Creates AIFunction instances for the "task-write" tool group.
    /// These tools mutate task state and are scoped to the calling agent.
    /// </summary>
    IReadOnlyList<AIFunction> CreateTaskWriteTools(string agentId, string agentName);

    /// <summary>
    /// Creates AIFunction instances for the "memory" tool group.
    /// Memory tools are scoped to the calling agent.
    /// </summary>
    IReadOnlyList<AIFunction> CreateMemoryTools(string agentId);

    /// <summary>
    /// Creates AIFunction instances for the "code-write" tool group.
    /// </summary>
    IReadOnlyList<AIFunction> CreateCodeWriteTools(string agentId, string agentName, AgentGitIdentity? gitIdentity = null, string? roomId = null);
}
