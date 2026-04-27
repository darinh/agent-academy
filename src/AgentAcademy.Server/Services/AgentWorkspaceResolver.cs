using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <inheritdoc/>
public sealed class AgentWorkspaceResolver : IAgentWorkspaceResolver
{
    /// <summary>
    /// Task statuses that count as "claimed and still in flight" for the
    /// purpose of routing the agent into a worktree. Excludes terminal
    /// states (<c>Completed</c>, <c>Cancelled</c>) where the worktree
    /// has either been merged or abandoned.
    /// </summary>
    private static readonly string[] InFlightStatuses =
    [
        nameof(Shared.Models.TaskStatus.Active),
        nameof(Shared.Models.TaskStatus.Blocked),
        nameof(Shared.Models.TaskStatus.AwaitingValidation),
        nameof(Shared.Models.TaskStatus.InReview),
        nameof(Shared.Models.TaskStatus.ChangesRequested),
        nameof(Shared.Models.TaskStatus.Approved),
        nameof(Shared.Models.TaskStatus.Merging),
    ];

    private readonly AgentAcademyDbContext _db;
    private readonly IWorktreeService _worktreeService;
    private readonly ILogger<AgentWorkspaceResolver> _logger;

    public AgentWorkspaceResolver(
        AgentAcademyDbContext db,
        IWorktreeService worktreeService,
        ILogger<AgentWorkspaceResolver> logger)
    {
        _db = db;
        _worktreeService = worktreeService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string?> ResolveAsync(
        string agentId, string roomId, string? roomWorkspacePath)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return roomWorkspacePath;

        // Scope claim lookup to the room's workspace (or the room itself when
        // the workspace is unknown). A stale claim in another workspace must
        // not redirect the agent's writes here. We accept the claim if either:
        //   (a) the task row has WorkspacePath == roomWorkspacePath, OR
        //   (b) the task row's RoomId matches the current roomId (covers tasks
        //       created via CREATE_TASK_ITEM that didn't propagate WorkspacePath
        //       — they're still scoped to a known room in this workspace), OR
        //   (c) both WorkspacePath and the matching room are null (no
        //       workspace association at all — single-room operator setup).
        var query = _db.Tasks.AsNoTracking()
            .Where(t => t.AssignedAgentId == agentId)
            .Where(t => t.BranchName != null && t.BranchName != "")
            .Where(t => InFlightStatuses.Contains(t.Status));

        if (!string.IsNullOrWhiteSpace(roomWorkspacePath))
        {
            query = query.Where(t =>
                t.WorkspacePath == roomWorkspacePath
                || (t.WorkspacePath == null && t.RoomId == roomId));
        }
        else
        {
            // No workspace context — only accept tasks tied to this room.
            query = query.Where(t => t.RoomId == roomId);
        }

        var matches = await query
            .Select(t => new { t.Id, t.BranchName, t.Title })
            .Take(2)
            .ToListAsync();

        if (matches.Count == 0)
            return roomWorkspacePath;

        if (matches.Count > 1)
        {
            // Ambiguity — fail closed by NOT routing. Write tools will refuse
            // with the standard "no worktree" message; the agent must release
            // tasks until exactly one remains. Surfacing this as a routing
            // override would silently pick one of N worktrees.
            _logger.LogWarning(
                "Agent {AgentId} has multiple in-flight claimed tasks in room {RoomId}; " +
                "skipping worktree routing. Agent should release tasks until exactly one remains.",
                agentId, roomId);
            return roomWorkspacePath;
        }

        var match = matches[0];
        try
        {
            // Idempotent: returns existing path when worktree already exists,
            // creates it from the existing branch otherwise.
            var info = await _worktreeService.CreateWorktreeAsync(match.BranchName!);
            _logger.LogDebug(
                "Resolved workspace for agent {AgentId} in room {RoomId} → worktree at {Path} " +
                "(claimed task {TaskId} '{Title}')",
                agentId, roomId, info.Path, match.Id, match.Title);
            return info.Path;
        }
        catch (Exception ex)
        {
            // Best-effort: if worktree provisioning fails (e.g. branch was
            // deleted out from under us), fall back to the room workspace.
            // The agent's writes will then refuse via P1.9 blocker D, which
            // surfaces the broken state to the agent rather than silently
            // contaminating develop.
            _logger.LogWarning(ex,
                "Failed to ensure worktree for agent {AgentId} claimed task {TaskId} (branch {Branch}); " +
                "falling back to room workspace path",
                agentId, match.Id, match.BranchName);
            return roomWorkspacePath;
        }
    }
}
