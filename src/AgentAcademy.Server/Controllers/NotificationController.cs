using AgentAcademy.Server.Notifications;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// REST endpoints for managing notification providers and sending test notifications.
/// </summary>
[ApiController]
[Route("api/notifications")]
public class NotificationController : ControllerBase
{
    private readonly NotificationManager _manager;
    private readonly ILogger<NotificationController> _logger;

    public NotificationController(NotificationManager manager, ILogger<NotificationController> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    /// <summary>
    /// Lists all registered notification providers with their current status.
    /// </summary>
    [HttpGet("providers")]
    public ActionResult<IEnumerable<ProviderStatusDto>> GetProviders()
    {
        var providers = _manager.GetAllProviders()
            .Select(p => new ProviderStatusDto(
                p.ProviderId,
                p.DisplayName,
                p.IsConfigured,
                p.IsConnected))
            .ToList();

        return Ok(providers);
    }

    /// <summary>
    /// Returns the configuration schema for a specific provider.
    /// </summary>
    [HttpGet("providers/{id}/schema")]
    public ActionResult<ProviderConfigSchema> GetSchema(string id)
    {
        var provider = _manager.GetProvider(id);
        if (provider is null)
        {
            return NotFound(new { error = $"Provider '{id}' not found" });
        }

        return Ok(provider.GetConfigSchema());
    }

    /// <summary>
    /// Applies configuration to a provider.
    /// </summary>
    [HttpPost("providers/{id}/configure")]
    public async Task<IActionResult> Configure(string id, [FromBody] Dictionary<string, string>? configuration, CancellationToken cancellationToken)
    {
        if (configuration is null)
        {
            return BadRequest(new { error = "Configuration dictionary is required" });
        }

        var provider = _manager.GetProvider(id);
        if (provider is null)
        {
            return NotFound(new { error = $"Provider '{id}' not found" });
        }

        try
        {
            await provider.ConfigureAsync(configuration, cancellationToken);
            return Ok(new { status = "configured", providerId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure provider '{ProviderId}'", id);
            return StatusCode(500, new { error = "Failed to configure provider" });
        }
    }

    /// <summary>
    /// Connects a provider.
    /// </summary>
    [HttpPost("providers/{id}/connect")]
    public async Task<IActionResult> Connect(string id, CancellationToken cancellationToken)
    {
        var provider = _manager.GetProvider(id);
        if (provider is null)
        {
            return NotFound(new { error = $"Provider '{id}' not found" });
        }

        try
        {
            await provider.ConnectAsync(cancellationToken);
            return Ok(new { status = "connected", providerId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect provider '{ProviderId}'", id);
            return StatusCode(500, new { error = "Failed to connect provider" });
        }
    }

    /// <summary>
    /// Disconnects a provider.
    /// </summary>
    [HttpPost("providers/{id}/disconnect")]
    public async Task<IActionResult> Disconnect(string id, CancellationToken cancellationToken)
    {
        var provider = _manager.GetProvider(id);
        if (provider is null)
        {
            return NotFound(new { error = $"Provider '{id}' not found" });
        }

        try
        {
            await provider.DisconnectAsync(cancellationToken);
            return Ok(new { status = "disconnected", providerId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect provider '{ProviderId}'", id);
            return StatusCode(500, new { error = "Failed to disconnect provider" });
        }
    }

    /// <summary>
    /// Sends a test notification to all connected providers.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> SendTestNotification(CancellationToken cancellationToken)
    {
        var testMessage = new NotificationMessage(
            Type: NotificationType.TaskComplete,
            Title: "Test Notification",
            Body: "This is a test notification from Agent Academy."
        );

        var successCount = await _manager.SendToAllAsync(testMessage, cancellationToken);

        return Ok(new
        {
            sent = successCount,
            totalConnected = _manager.GetAllProviders().Count(p => p.IsConnected)
        });
    }
}

/// <summary>
/// DTO for provider status in API responses.
/// </summary>
public record ProviderStatusDto(
    string ProviderId,
    string DisplayName,
    bool IsConfigured,
    bool IsConnected
);
