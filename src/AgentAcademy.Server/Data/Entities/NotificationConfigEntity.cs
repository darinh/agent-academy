namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Persists notification provider configuration (e.g., Discord bot token, channel ID)
/// so providers can be auto-configured and reconnected on server restart.
/// </summary>
public class NotificationConfigEntity
{
    /// <summary>Row identifier.</summary>
    public int Id { get; set; }

    /// <summary>Provider identifier (e.g., "discord", "console").</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>Configuration key (e.g., "bot_token", "channel_id").</summary>
    public string Key { get; set; } = "";

    /// <summary>Configuration value.</summary>
    public string Value { get; set; } = "";

    /// <summary>When this config entry was last updated.</summary>
    public DateTime UpdatedAt { get; set; }
}
