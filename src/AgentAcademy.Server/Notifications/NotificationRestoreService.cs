using AgentAcademy.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// One-shot background service that restores notification provider configurations
/// from the database on startup. Providers must already be registered synchronously
/// in the DI pipeline — this service only handles the async restore/connect step.
/// </summary>
public sealed class NotificationRestoreService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NotificationManager _notificationManager;
    private readonly ILogger<NotificationRestoreService> _logger;

    public NotificationRestoreService(
        IServiceScopeFactory scopeFactory,
        NotificationManager notificationManager,
        ILogger<NotificationRestoreService> logger)
    {
        _scopeFactory = scopeFactory;
        _notificationManager = notificationManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RestoreProviderConfigsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification provider auto-restore failed — providers will need manual reconfiguration");
        }
    }

    private async Task RestoreProviderConfigsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<ConfigEncryptionService>();

        // Load all configs first, then group in-memory (EF Core may not translate
        // GroupBy with element projections to SQL for all providers).
        var allConfigs = await db.NotificationConfigs.ToListAsync(stoppingToken);
        var savedConfigs = allConfigs
            .GroupBy(c => c.ProviderId)
            .ToList();

        foreach (var group in savedConfigs)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            var provider = _notificationManager.GetProvider(group.Key);
            if (provider is null)
                continue;

            var schema = provider.GetConfigSchema();
            var secretKeys = schema.Fields
                .Where(f => string.Equals(f.Type, "secret", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var config = new Dictionary<string, string>();
            var failedKeys = new List<string>();
            foreach (var entry in group)
            {
                if (secretKeys.Contains(entry.Key))
                {
                    if (encryption.TryDecrypt(entry.Value, out var decrypted))
                        config[entry.Key] = decrypted;
                    else
                        failedKeys.Add(entry.Key);
                }
                else
                {
                    config[entry.Key] = entry.Value;
                }
            }

            if (failedKeys.Count > 0)
            {
                _logger.LogWarning(
                    "Notification provider '{ProviderId}' has undecryptable config keys: {Keys}. Reconfiguration required.",
                    group.Key, string.Join(", ", failedKeys));
                continue;
            }

            try
            {
                await provider.ConfigureAsync(config);
                await provider.ConnectAsync();
                _logger.LogInformation(
                    "Auto-restored notification provider '{ProviderId}' from saved config",
                    group.Key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to auto-restore notification provider '{ProviderId}'",
                    group.Key);
            }
        }
    }
}
