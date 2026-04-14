namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a collaboration task.
/// Maps to the "tasks" table.
/// </summary>
public class TaskEntity
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SuccessCriteria { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string Type { get; set; } = "Feature";
    public string CurrentPhase { get; set; } = "Planning";
    public string CurrentPlan { get; set; } = string.Empty;
    public string ValidationStatus { get; set; } = "NotStarted";
    public string ValidationSummary { get; set; } = string.Empty;
    public string ImplementationStatus { get; set; } = "NotStarted";
    public string ImplementationSummary { get; set; } = string.Empty;
    public string PreferredRoles { get; set; } = "[]";
    public string? RoomId { get; set; }
    public string? WorkspacePath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Extended task metadata
    public string? Size { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? AssignedAgentId { get; set; }
    public string? AssignedAgentName { get; set; }
    public bool UsedFleet { get; set; }
    public string FleetModels { get; set; } = "[]";
    public string? BranchName { get; set; }
    public string? PullRequestUrl { get; set; }
    public int? PullRequestNumber { get; set; }
    public string? PullRequestStatus { get; set; }
    public string? ReviewerAgentId { get; set; }
    public int ReviewRounds { get; set; }
    public string TestsCreated { get; set; } = "[]";
    public int CommitCount { get; set; }
    public string? MergeCommitSha { get; set; }

    // Sprint association
    public string? SprintId { get; set; }

    /// <summary>Priority level (0=Critical, 1=High, 2=Medium, 3=Low). Stored as int for correct sort order.</summary>
    public int Priority { get; set; } = 2; // Medium

    // Navigation properties
    public RoomEntity? Room { get; set; }
    public SprintEntity? Sprint { get; set; }

    /// <summary>Tasks that this task depends on (must complete before this task can start).</summary>
    public ICollection<TaskDependencyEntity> Dependencies { get; set; } = new List<TaskDependencyEntity>();

    /// <summary>Tasks that depend on this task (blocked until this task completes).</summary>
    public ICollection<TaskDependencyEntity> Dependents { get; set; } = new List<TaskDependencyEntity>();
}
