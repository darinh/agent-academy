namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a sprint — a numbered iteration of the build cycle.
/// Maps to the "sprints" table.
/// </summary>
public class SprintEntity
{
    public string Id { get; set; } = string.Empty;
    public int Number { get; set; }
    public string WorkspacePath { get; set; } = string.Empty;
    public string Status { get; set; } = "Active"; // Active | Completed | Cancelled
    public string CurrentStage { get; set; } = "Intake"; // Intake | Planning | Discussion | Validation | Implementation | FinalSynthesis
    public string? OverflowFromSprintId { get; set; }
    public bool AwaitingSignOff { get; set; }
    public string? PendingStage { get; set; }
    public DateTime? SignOffRequestedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    // Blocked signal (P1.4 narrow scope). When BlockedAt is non-null, the sprint
    // is still Status="Active" — agents/orchestrator are paused waiting on a
    // human or external resolution. Cleared on UnblockSprintAsync.
    public DateTime? BlockedAt { get; set; }
    public string? BlockReason { get; set; }

    // Self-drive accounting (P1.2, p1-2-self-drive-design.md §3.1). All counters
    // increment by ConversationRoundRunner after each trigger run; RoundsThisStage
    // and SelfDriveContinuations reset on stage transition. Defaults are zero/null
    // so existing rows backfill cleanly with no schema migration data step.
    public int RoundsThisSprint { get; set; }
    public int RoundsThisStage { get; set; }
    public int SelfDriveContinuations { get; set; }
    public DateTime? LastRoundCompletedAt { get; set; }

    // Per-sprint override of Orchestrator:SelfDrive:MaxRoundsPerSprint
    // (p1-2-self-drive-design.md §6). Null means use the configured default.
    public int? MaxRoundsOverride { get; set; }

    // Navigation
    public SprintEntity? OverflowFromSprint { get; set; }
}
