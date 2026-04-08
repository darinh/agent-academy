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

    // Navigation properties
    public RoomEntity? Room { get; set; }
}
