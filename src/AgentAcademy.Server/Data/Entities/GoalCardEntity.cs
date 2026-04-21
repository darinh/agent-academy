namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a goal card — a structured intent artifact an agent
/// creates before starting significant work. Content is immutable after creation;
/// only the Status field transitions. Maps to the "goal_cards" table.
/// </summary>
public class GoalCardEntity
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The agent that created this goal card.</summary>
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;

    /// <summary>The room context where this work is happening. Required.</summary>
    public string RoomId { get; set; } = string.Empty;

    /// <summary>The task this card is associated with, if one exists yet. Nullable — cards may precede task creation.</summary>
    public string? TaskId { get; set; }

    /// <summary>What the user/system asked for.</summary>
    public string TaskDescription { get; set; } = string.Empty;

    /// <summary>Why the agent believes the user wants it.</summary>
    public string Intent { get; set; } = string.Empty;

    /// <summary>Do task and intent point the same direction? If divergent — flag it.</summary>
    public string Divergence { get; set; } = string.Empty;

    /// <summary>One paragraph defending the work.</summary>
    public string Steelman { get; set; } = string.Empty;

    /// <summary>One paragraph attacking the work — simpler alternatives, root-cause vs symptom, assumptions.</summary>
    public string Strawman { get; set; } = string.Empty;

    /// <summary>Proceed / ProceedWithCaveat / Challenge</summary>
    public string Verdict { get; set; } = "Proceed";

    /// <summary>Fresh eyes Q1: "If I had no context, would this request make sense on its own?"</summary>
    public string FreshEyes1 { get; set; } = string.Empty;

    /// <summary>Fresh eyes Q2: "Is there any part that, if it succeeded, would not move the user toward their bigger goal?"</summary>
    public string FreshEyes2 { get; set; } = string.Empty;

    /// <summary>Fresh eyes Q3: "Would a thoughtful peer ask 'why are we doing this?'"</summary>
    public string FreshEyes3 { get; set; } = string.Empty;

    /// <summary>Version of the prompt template used to generate this card (for future schema evolution).</summary>
    public int PromptVersion { get; set; } = 1;

    /// <summary>Active / Completed / Challenged / Abandoned</summary>
    public string Status { get; set; } = "Active";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public RoomEntity? Room { get; set; }
    public TaskEntity? Task { get; set; }
}
