namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persistence entity for an activity/audit event.
/// Maps to the "activity_events" table.
/// </summary>
public class ActivityEventEntity
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public string? RoomId { get; set; }
    public string? ActorId { get; set; }
    public string? TaskId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public DateTime OccurredAt { get; set; }

    /// <summary>JSON-serialized metadata payload for structured event data.</summary>
    public string? MetadataJson { get; set; }

    // Navigation properties
    public RoomEntity? Room { get; set; }
}
