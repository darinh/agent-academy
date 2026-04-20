using System.ComponentModel.DataAnnotations;

namespace AgentAcademy.Shared.Models;

/// <summary>
/// Snapshot of a goal card — the structured intent artifact an agent creates
/// before starting significant work. Captures task vs. intent analysis for
/// drift detection. Content is immutable after creation; only status transitions.
/// </summary>
public record GoalCard(
    string Id,
    string AgentId,
    string AgentName,
    string RoomId,
    string? TaskId,
    string TaskDescription,
    string Intent,
    string Divergence,
    string Steelman,
    string Strawman,
    GoalCardVerdict Verdict,
    string FreshEyes1,
    string FreshEyes2,
    string FreshEyes3,
    int PromptVersion,
    GoalCardStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

/// <summary>
/// Request to create a goal card. All content fields are required.
/// </summary>
public record CreateGoalCardRequest(
    [Required, StringLength(2000, MinimumLength = 10)]
    string TaskDescription,

    [Required, StringLength(2000, MinimumLength = 10)]
    string Intent,

    [Required, StringLength(500, MinimumLength = 5)]
    string Divergence,

    [Required, StringLength(2000, MinimumLength = 20)]
    string Steelman,

    [Required, StringLength(2000, MinimumLength = 20)]
    string Strawman,

    [Required]
    GoalCardVerdict Verdict,

    [Required, StringLength(1000, MinimumLength = 10)]
    string FreshEyes1,

    [Required, StringLength(1000, MinimumLength = 10)]
    string FreshEyes2,

    [Required, StringLength(1000, MinimumLength = 10)]
    string FreshEyes3,

    string? TaskId = null
);

/// <summary>
/// Request to update the status of a goal card.
/// </summary>
public record UpdateGoalCardStatusRequest(
    [Required] GoalCardStatus Status
);
