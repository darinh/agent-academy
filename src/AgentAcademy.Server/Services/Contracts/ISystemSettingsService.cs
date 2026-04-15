namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Abstraction for typed access to system settings.
/// Static constants and defaults remain on the concrete
/// <see cref="SystemSettingsService"/> class.
/// </summary>
public interface ISystemSettingsService
{
    Task<T> GetAsync<T>(string key, T defaultValue);
    Task SetAsync(string key, string value);
    Task<Dictionary<string, string>> GetAllAsync();
    Task<Dictionary<string, string>> GetAllWithDefaultsAsync();
    Task<int> GetMainRoomEpochSizeAsync();
    Task<int> GetBreakoutEpochSizeAsync();
    Task<int> GetRateLimitMaxCommandsAsync();
    Task<int> GetRateLimitWindowSecondsAsync();
    Task<bool> GetSprintAutoStartAsync();
    Task<int> GetDigestThresholdAsync();
}
