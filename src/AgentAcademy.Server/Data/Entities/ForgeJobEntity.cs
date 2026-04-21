namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for a forge pipeline job.
/// Provides durability across server restarts — jobs survive process recycling.
/// Maps to the "forge_jobs" table.
/// </summary>
public class ForgeJobEntity
{
    public string Id { get; set; } = string.Empty;
    public string? RunId { get; set; }
    public string Status { get; set; } = "queued";
    public string? Error { get; set; }
    public string TaskBriefJson { get; set; } = "{}";
    public string MethodologyJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
