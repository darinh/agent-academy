using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// REST endpoints for managing notification providers and sending test notifications.
/// </summary>
[ApiController]
[Route("api/notifications")]
public class NotificationController : ControllerBase
{
    private readonly NotificationManager _manager;
    private readonly NotificationDeliveryTracker _tracker;
    private readonly ConfigEncryptionService _encryption;
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<NotificationController> _logger;

    public NotificationController(
        NotificationManager manager,
        NotificationDeliveryTracker tracker,
        ConfigEncryptionService encryption,
        AgentAcademyDbContext db,
        ILogger<NotificationController> logger)
    {
        _manager = manager;
        _tracker = tracker;
        _encryption = encryption;
        _db = db;
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

            // Determine which fields are secrets from the provider schema
            var schema = provider.GetConfigSchema();
            var secretKeys = schema.Fields
                .Where(f => string.Equals(f.Type, "secret", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Persist config to DB for auto-restore on restart (atomic upsert).
            // Secret fields are encrypted before storage.
            var now = DateTime.UtcNow;
            foreach (var (key, value) in configuration)
            {
                var storedValue = secretKeys.Contains(key)
                    ? _encryption.Encrypt(value)
                    : value;

                await _db.Database.ExecuteSqlRawAsync(
                    @"INSERT INTO notification_configs (ProviderId, [Key], Value, UpdatedAt)
                      VALUES ({0}, {1}, {2}, {3})
                      ON CONFLICT(ProviderId, [Key]) DO UPDATE SET Value = {2}, UpdatedAt = {3}",
                    [id, key, storedValue, now],
                    cancellationToken);
            }
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

    /// <summary>
    /// Queries notification delivery history with optional filters.
    /// </summary>
    [HttpGet("deliveries")]
    public async Task<ActionResult<IEnumerable<NotificationDeliveryDto>>> GetDeliveries(
        [FromQuery] string? channel = null,
        [FromQuery] string? providerId = null,
        [FromQuery] string? status = null,
        [FromQuery] string? roomId = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var deliveries = await _tracker.GetDeliveriesAsync(channel, providerId, status, roomId, limit, offset);
        var dtos = deliveries.Select(d => new NotificationDeliveryDto(
            d.Id, d.Channel, d.Title, d.Body, d.RoomId, d.AgentId,
            d.ProviderId, d.Status, d.Error, d.AttemptedAt));
        return Ok(dtos);
    }

    /// <summary>
    /// Returns delivery statistics grouped by status for a time window.
    /// </summary>
    [HttpGet("deliveries/stats")]
    public async Task<ActionResult<Dictionary<string, int>>> GetDeliveryStats(
        [FromQuery] int? hours = 24)
    {
        var window = TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 720));
        var stats = await _tracker.GetDeliveryStatsAsync(window);
        return Ok(stats);
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

/// <summary>
/// DTO for notification delivery history in API responses.
/// </summary>
public record NotificationDeliveryDto(
    int Id,
    string Channel,
    string? Title,
    string? Body,
    string? RoomId,
    string? AgentId,
    string ProviderId,
    string Status,
    string? Error,
    DateTime AttemptedAt
);
