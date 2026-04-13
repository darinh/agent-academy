namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a recurring sprint schedule scoped to a workspace.
/// Uses a 5-field cron expression to determine when new sprints should be created.
/// Maps to the "sprint_schedules" table.
/// </summary>
public class SprintScheduleEntity
{
    public string Id { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>IANA timezone ID (e.g. "America/New_York"). Defaults to "UTC".</summary>
    public string TimeZoneId { get; set; } = "UTC";

    public bool Enabled { get; set; }

    /// <summary>Precomputed next evaluation time in UTC, derived from cron + timezone.</summary>
    public DateTime? NextRunAtUtc { get; set; }

    /// <summary>UTC timestamp of the last successful sprint creation from this schedule.</summary>
    public DateTime? LastTriggeredAt { get; set; }

    /// <summary>UTC timestamp of the most recent evaluation attempt (success or skip).</summary>
    public DateTime? LastEvaluatedAt { get; set; }

    /// <summary>Outcome of the last evaluation: "started", "skipped_active", "error".</summary>
    public string? LastOutcome { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
