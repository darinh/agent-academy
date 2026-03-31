using AgentAcademy.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// REST endpoints for system-wide settings management.
/// </summary>
[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly SystemSettingsService _settings;

    public SettingsController(SystemSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// GET /api/settings — returns all settings with defaults filled in.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Dictionary<string, string>>> GetAll()
    {
        var settings = await _settings.GetAllWithDefaultsAsync();
        return Ok(settings);
    }

    /// <summary>
    /// GET /api/settings/{key} — returns a single setting value.
    /// </summary>
    [HttpGet("{key}")]
    public async Task<ActionResult<SettingResponse>> GetSetting(string key)
    {
        var allDefaults = await _settings.GetAllWithDefaultsAsync();
        if (!allDefaults.TryGetValue(key, out var value))
            return NotFound();

        return Ok(new SettingResponse(key, value));
    }

    /// <summary>
    /// PUT /api/settings — bulk upsert settings.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> UpsertSettings([FromBody] Dictionary<string, string> settings)
    {
        foreach (var (key, value) in settings)
        {
            await _settings.SetAsync(key, value);
        }
        return Ok(await _settings.GetAllWithDefaultsAsync());
    }
}

public record SettingResponse(string Key, string Value);
