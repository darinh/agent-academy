using AgentAcademy.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Provides typed access to system-wide settings stored in the database.
/// Settings are key-value pairs with sensible defaults for unconfigured keys.
/// </summary>
public sealed class SystemSettingsService
{
    private readonly AgentAcademyDbContext _db;

    // Well-known setting keys
    public const string MainRoomEpochSizeKey = "conversation.mainRoomEpochSize";
    public const string BreakoutEpochSizeKey = "conversation.breakoutEpochSize";
    public const string RateLimitMaxCommandsKey = "commands.rateLimitMaxCommands";
    public const string RateLimitWindowSecondsKey = "commands.rateLimitWindowSeconds";
    public const string SprintAutoStartKey = "sprint.autoStartOnCompletion";

    // Defaults
    public const int DefaultMainRoomEpochSize = 50;
    public const int DefaultBreakoutEpochSize = 30;
    public const int DefaultRateLimitMaxCommands = 30;
    public const int DefaultRateLimitWindowSeconds = 60;
    public const bool DefaultSprintAutoStart = false;

    public SystemSettingsService(AgentAcademyDbContext db)
    {
        _db = db;
    }

    public async Task<T> GetAsync<T>(string key, T defaultValue)
    {
        var entity = await _db.SystemSettings.FindAsync(key);
        if (entity is null) return defaultValue;

        try
        {
            return (T)Convert.ChangeType(entity.Value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }

    public async Task SetAsync(string key, string value)
    {
        var entity = await _db.SystemSettings.FindAsync(key);
        if (entity is null)
        {
            entity = new Data.Entities.SystemSettingEntity
            {
                Key = key,
                Value = value,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.SystemSettings.Add(entity);
        }
        else
        {
            entity.Value = value;
            entity.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        var settings = await _db.SystemSettings.ToListAsync();
        return settings.ToDictionary(s => s.Key, s => s.Value);
    }

    /// <summary>
    /// Returns all settings with defaults filled in for known keys.
    /// </summary>
    public async Task<Dictionary<string, string>> GetAllWithDefaultsAsync()
    {
        var stored = await GetAllAsync();

        var defaults = new Dictionary<string, string>
        {
            [MainRoomEpochSizeKey] = DefaultMainRoomEpochSize.ToString(),
            [BreakoutEpochSizeKey] = DefaultBreakoutEpochSize.ToString(),
            [RateLimitMaxCommandsKey] = DefaultRateLimitMaxCommands.ToString(),
            [RateLimitWindowSecondsKey] = DefaultRateLimitWindowSeconds.ToString(),
            [SprintAutoStartKey] = DefaultSprintAutoStart.ToString(),
        };

        foreach (var (key, value) in stored)
            defaults[key] = value;

        return defaults;
    }

    public async Task<int> GetMainRoomEpochSizeAsync()
        => await GetAsync(MainRoomEpochSizeKey, DefaultMainRoomEpochSize);

    public async Task<int> GetBreakoutEpochSizeAsync()
        => await GetAsync(BreakoutEpochSizeKey, DefaultBreakoutEpochSize);

    public async Task<int> GetRateLimitMaxCommandsAsync()
        => await GetAsync(RateLimitMaxCommandsKey, DefaultRateLimitMaxCommands);

    public async Task<int> GetRateLimitWindowSecondsAsync()
        => await GetAsync(RateLimitWindowSecondsKey, DefaultRateLimitWindowSeconds);

    public async Task<bool> GetSprintAutoStartAsync()
        => await GetAsync(SprintAutoStartKey, DefaultSprintAutoStart);
}
