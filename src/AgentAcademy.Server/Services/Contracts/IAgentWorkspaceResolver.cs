namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Resolves the effective per-agent workspace path for a turn. Closes
/// P1.9 blocker D: when an agent has a currently-claimed non-terminal task
/// with a branch, route them into that task's worktree (so subsequent
/// <c>write_file</c>/<c>commit_changes</c> tools land in the worktree
/// instead of contaminating the develop checkout).
/// </summary>
/// <remarks>
/// Resolution rules:
/// <list type="bullet">
///   <item>Zero claimed tasks for the agent in scope → returns
///   <paramref name="roomWorkspacePath"/> unchanged (existing behaviour;
///   write tools refuse against develop per P1.9 blocker D).</item>
///   <item>Exactly one claimed non-terminal task with a branch → ensures
///   the worktree exists on disk (idempotent) and returns its path.</item>
///   <item>Multiple claimed tasks → returns
///   <paramref name="roomWorkspacePath"/> unchanged (write tools will refuse
///   with the standard message; the agent must release tasks until at most
///   one remains, then their next turn auto-routes).</item>
/// </list>
/// Scoping: claimed tasks are filtered by the agent and by the room's
/// workspace (via the task row's <c>WorkspacePath</c>, with fallback through
/// the task's room) so a stale claim in another workspace cannot hijack
/// routing.
/// </remarks>
public interface IAgentWorkspaceResolver
{
    /// <summary>
    /// Returns the effective workspace path the agent should use for tools
    /// in their next turn within <paramref name="roomId"/>.
    /// </summary>
    Task<string?> ResolveAsync(
        string agentId, string roomId, string? roomWorkspacePath);
}
