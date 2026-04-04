using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Records notification delivery attempts to the database for observability.
/// Uses <see cref="IServiceScopeFactory"/> to create scoped DbContext instances,
/// since the tracker is consumed by the singleton <see cref="NotificationManager"/>.
/// </summary>
public sealed class NotificationDeliveryTracker
{
    private const int MaxBodyLength = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationDeliveryTracker> _logger;

    public NotificationDeliveryTracker(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationDeliveryTracker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Records a successful delivery to a specific provider.
    /// </summary>
    public Task RecordDeliveryAsync(
        string channel,
        string providerId,
        string? title,
        string? body,
        string? roomId,
        string? agentId)
    {
        return RecordAsync(channel, providerId, "Delivered", title, body, roomId, agentId, error: null);
    }

    /// <summary>
    /// Records a provider returning false (skipped/unable to deliver).
    /// </summary>
    public Task RecordSkippedAsync(
        string channel,
        string providerId,
        string? title,
        string? body,
        string? roomId,
        string? agentId)
    {
        return RecordAsync(channel, providerId, "Skipped", title, body, roomId, agentId, error: null);
    }

    /// <summary>
    /// Records a delivery failure after all retries are exhausted.
    /// </summary>
    public Task RecordFailureAsync(
        string channel,
        string providerId,
        string? title,
        string? body,
        string? roomId,
        string? agentId,
        string error)
    {
        return RecordAsync(channel, providerId, "Failed", title, body, roomId, agentId, error);
    }

    /// <summary>
    /// Queries delivery history with optional filters.
    /// </summary>
    public async Task<List<NotificationDeliveryEntity>> GetDeliveriesAsync(
        string? channel = null,
        string? providerId = null,
        string? status = null,
        string? roomId = null,
        int limit = 50,
        int offset = 0)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var query = db.NotificationDeliveries.AsQueryable();

        if (channel is not null)
            query = query.Where(d => d.Channel == channel);
        if (providerId is not null)
            query = query.Where(d => d.ProviderId == providerId);
        if (status is not null)
            query = query.Where(d => d.Status == status);
        if (roomId is not null)
            query = query.Where(d => d.RoomId == roomId);

        return await query
            .OrderByDescending(d => d.AttemptedAt)
            .ThenByDescending(d => d.Id)
            .Skip(offset)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync();
    }

    /// <summary>
    /// Gets delivery statistics grouped by status for a time window.
    /// </summary>
    public async Task<Dictionary<string, int>> GetDeliveryStatsAsync(TimeSpan? window = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var cutoff = DateTime.UtcNow - (window ?? TimeSpan.FromHours(24));

        return await db.NotificationDeliveries
            .Where(d => d.AttemptedAt >= cutoff)
            .GroupBy(d => d.Status)
            .ToDictionaryAsync(g => g.Key, g => g.Count());
    }

    private async Task RecordAsync(
        string channel,
        string providerId,
        string status,
        string? title,
        string? body,
        string? roomId,
        string? agentId,
        string? error)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            db.NotificationDeliveries.Add(new NotificationDeliveryEntity
            {
                Channel = channel,
                ProviderId = providerId,
                Title = title,
                Body = Truncate(body, MaxBodyLength),
                RoomId = roomId,
                AgentId = agentId,
                Status = status,
                Error = error,
                AttemptedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Delivery tracking must never break notification flow
            _logger.LogWarning(ex, "Failed to record notification delivery for {Channel}/{ProviderId}", channel, providerId);
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
            return value;
        return value[..maxLength];
    }
}
