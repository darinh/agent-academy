using System.Text.Json.Serialization;

namespace AgentAcademy.Shared.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SprintStage
{
    Intake,
    Planning,
    Discussion,
    Validation,
    Implementation,
    FinalSynthesis
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SprintStatus
{
    Active,
    Completed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactType
{
    RequirementsDocument,
    SprintPlan,
    ValidationReport,
    SprintReport,
    OverflowRequirements
}

public record SprintSnapshot(
    string Id,
    int Number,
    SprintStatus Status,
    SprintStage CurrentStage,
    string? OverflowFromSprintId,
    bool AwaitingSignOff,
    SprintStage? PendingStage,
    DateTime? SignOffRequestedAt,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    DateTime? BlockedAt = null,
    string? BlockReason = null);

public record SprintArtifact(
    int Id,
    string SprintId,
    SprintStage Stage,
    ArtifactType Type,
    string Content,
    string? CreatedByAgentId,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record RequirementsDocument(
    string Title,
    string Description,
    List<string> InScope,
    List<string> OutOfScope);

public record SprintPlanDocument(
    string Summary,
    List<SprintPlanPhase> Phases,
    List<string>? OverflowRequirements);

public record SprintPlanPhase(
    string Name,
    string Description,
    List<string> Deliverables);

public record ValidationReport(
    string Verdict,
    List<string> Findings,
    List<string>? RequiredChanges);

public record SprintReport(
    string Summary,
    List<string> Delivered,
    List<string> Learnings,
    List<string>? OverflowRequirements);

// ── Sprint Metrics ──────────────────────────────────────────

/// <summary>
/// Aggregated metrics for a single sprint: duration, stage timing,
/// task and artifact counts.
/// </summary>
public record SprintMetrics(
    string SprintId,
    int SprintNumber,
    SprintStatus Status,
    double? DurationSeconds,
    int StageTransitions,
    int ArtifactCount,
    int TaskCount,
    int CompletedTaskCount,
    Dictionary<string, double> TimePerStageSeconds,
    DateTime CreatedAt,
    DateTime? CompletedAt);

/// <summary>
/// Workspace-level rollup of sprint metrics across all sprints.
/// </summary>
public record SprintMetricsSummary(
    int TotalSprints,
    int CompletedSprints,
    int CancelledSprints,
    int ActiveSprints,
    double? AverageDurationSeconds,
    double AverageTaskCount,
    double AverageArtifactCount,
    Dictionary<string, double> AverageTimePerStageSeconds);

/// <summary>
/// Configuration for sprint timeout behavior. A background service
/// periodically checks for stale sprints and auto-rejects or auto-cancels.
/// </summary>
public sealed class SprintTimeoutSettings
{
    public const string SectionName = "SprintTimeouts";

    /// <summary>Whether timeout checking is enabled. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes a sprint can sit in AwaitingSignOff before auto-reject. Must be &gt; 0. Default: 240 (4 hours).</summary>
    public int SignOffTimeoutMinutes { get; set; } = 240;

    /// <summary>Hours before an active sprint is auto-cancelled. Must be &gt; 0. Default: 48.</summary>
    public int MaxSprintDurationHours { get; set; } = 48;

    /// <summary>Minutes between timeout checks. Must be &gt; 0. Default: 5.</summary>
    public int CheckIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Validates settings at startup. Throws if values would cause dangerous behavior.
    /// </summary>
    public void Validate()
    {
        if (CheckIntervalMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(CheckIntervalMinutes),
                CheckIntervalMinutes, "Must be > 0.");
        if (SignOffTimeoutMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(SignOffTimeoutMinutes),
                SignOffTimeoutMinutes, "Must be > 0.");
        if (MaxSprintDurationHours <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxSprintDurationHours),
                MaxSprintDurationHours, "Must be > 0.");
    }
}

public sealed class SprintSchedulerSettings
{
    public const string SectionName = "SprintScheduler";

    /// <summary>Whether the scheduler background service is enabled. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between schedule evaluation passes. Must be &gt; 0. Default: 60.</summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    public void Validate()
    {
        if (CheckIntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(CheckIntervalSeconds),
                CheckIntervalSeconds, "Must be > 0.");
    }
}

/// <summary>REST API model for creating/updating a sprint schedule.</summary>
public sealed record SprintScheduleRequest(
    string CronExpression,
    string TimeZoneId = "UTC",
    bool Enabled = true);

/// <summary>REST API model for blocking a sprint with a reason.</summary>
public sealed record BlockSprintRequest(string Reason);

/// <summary>REST API model returned for sprint schedule queries.</summary>
public sealed record SprintScheduleResponse(
    string Id,
    string WorkspacePath,
    string CronExpression,
    string TimeZoneId,
    bool Enabled,
    DateTime? NextRunAtUtc,
    DateTime? LastTriggeredAt,
    DateTime? LastEvaluatedAt,
    string? LastOutcome,
    DateTime CreatedAt,
    DateTime UpdatedAt);
