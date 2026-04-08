namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Records every outbound notification delivery attempt per provider.
/// Provides a persistent audit trail for notification observability.
/// </summary>
public class NotificationDeliveryEntity
{
    public int Id { get; set; }

    /// <summary>
    /// The delivery channel: "Broadcast", "AgentQuestion", "DirectMessage", "RoomRenamed".
    /// </summary>
    public string Channel { get; set; } = "";

    /// <summary>Notification title, agent question summary, or room rename description.</summary>
    public string? Title { get; set; }

    /// <summary>Message body (truncated to 500 chars for storage efficiency).</summary>
    public string? Body { get; set; }

    /// <summary>Room context for the notification, if applicable.</summary>
    public string? RoomId { get; set; }

    /// <summary>Agent that triggered or is associated with the notification.</summary>
    public string? AgentId { get; set; }

    /// <summary>Provider that attempted delivery (e.g., "discord", "console").</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>Delivery outcome: "Delivered", "Failed", "Skipped".</summary>
    public string Status { get; set; } = "Delivered";

    /// <summary>Error message when Status is "Failed".</summary>
    public string? Error { get; set; }

    /// <summary>When the delivery was attempted.</summary>
    public DateTime AttemptedAt { get; set; }
}
