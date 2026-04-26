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
    /// When <paramref name="workspacePath"/> is provided the read tools resolve
    /// paths and run searches inside that worktree.
    /// </summary>
    IReadOnlyList<AIFunction> CreateCodeTools(string? workspacePath = null);

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
    /// When <paramref name="workspacePath"/> is provided the wrapper writes and
    /// commits inside that worktree instead of the develop checkout.
    /// </summary>
    IReadOnlyList<AIFunction> CreateCodeWriteTools(string agentId, string agentName, AgentGitIdentity? gitIdentity = null, string? roomId = null, string? workspacePath = null);

    /// <summary>
    /// Creates AIFunction instances for the "spec-write" tool group.
    /// Writes are restricted to the <c>specs/</c> directory. Typically granted
    /// to the Technical Writer (Thucydides). When <paramref name="workspacePath"/>
    /// is provided the wrapper writes and commits inside that worktree.
    /// </summary>
    IReadOnlyList<AIFunction> CreateSpecWriteTools(string agentId, string agentName, AgentGitIdentity? gitIdentity = null, string? roomId = null, string? workspacePath = null);
}
