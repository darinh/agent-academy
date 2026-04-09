namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Tracks a per-agent worktree within a workspace. Composite PK: (WorkspacePath, AgentId).
/// Each agent gets its own isolated git worktree directory so branch switching
/// in one agent's breakout loop doesn't affect another agent's working tree.
/// </summary>
public class AgentWorkspaceEntity
{
    public string WorkspacePath { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Absolute path to the agent's worktree directory on disk.</summary>
    public string? WorktreePath { get; set; }

    /// <summary>The branch currently checked out in the agent's worktree.</summary>
    public string? CurrentBranch { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }

    // Navigation
    public WorkspaceEntity? Workspace { get; set; }
}
