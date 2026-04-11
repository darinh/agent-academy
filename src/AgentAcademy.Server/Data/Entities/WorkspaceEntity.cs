namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for onboarded workspace/project metadata.
/// Maps to the "workspaces" table. Uses Path as primary key.
/// </summary>
public class WorkspaceEntity
{
    public string Path { get; set; } = string.Empty;
    public string? ProjectName { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Git remote origin URL (e.g. "https://github.com/org/repo.git").</summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>Default/integration branch name (e.g. "develop", "main").</summary>
    public string? DefaultBranch { get; set; }

    /// <summary>Git hosting provider: "github", "azure-devops", "gitlab", "bitbucket", or null.</summary>
    public string? HostProvider { get; set; }

    /// <summary>Agent worktrees associated with this workspace.</summary>
    public List<AgentWorkspaceEntity> AgentWorktrees { get; set; } = [];
}
