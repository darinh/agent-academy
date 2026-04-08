namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Key-value store for system-wide settings.
/// Provides configurable values for features like conversation epoch sizes.
/// Maps to the "system_settings" table.
/// </summary>
public class SystemSettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
