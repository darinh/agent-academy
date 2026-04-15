using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Persists OAuth tokens to the database (encrypted via Data Protection)
/// so the server can restore them after a restart without requiring
/// a browser visit. Subscribes to <see cref="ICopilotTokenProvider.TokenChanged"/>
/// to persist automatically on every token refresh.
/// </summary>
public sealed class TokenPersistenceService : IHostedService, IDisposable
{
    private const string Purpose = "AgentAcademy.AuthTokens";
    private const string AccessTokenKey = "auth:accessToken";
    private const string RefreshTokenKey = "auth:refreshToken";
    private const string ExpiresAtKey = "auth:expiresAt";
    private const string RefreshTokenExpiresAtKey = "auth:refreshTokenExpiresAt";

    private readonly ICopilotTokenProvider _tokenProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<TokenPersistenceService> _logger;
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private volatile bool _isRestoring;
    private volatile bool _disposed;

    public TokenPersistenceService(
        ICopilotTokenProvider tokenProvider,
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider dataProtection,
        ILogger<TokenPersistenceService> logger)
    {
        _tokenProvider = tokenProvider;
        _scopeFactory = scopeFactory;
        _protector = dataProtection.CreateProtector(Purpose);
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RestoreTokensAsync(cancellationToken);
        _tokenProvider.TokenChanged += OnTokenChanged;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tokenProvider.TokenChanged -= OnTokenChanged;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _disposed = true;
        _tokenProvider.TokenChanged -= OnTokenChanged;
        _persistLock.Dispose();
    }

    private async void OnTokenChanged()
    {
        if (_isRestoring || _disposed) return;

        try
        {
            await PersistTokensAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist tokens to database — will retry on next change");
        }
    }

    internal async Task RestoreTokensAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            var keys = new[] { AccessTokenKey, RefreshTokenKey, ExpiresAtKey, RefreshTokenExpiresAtKey };
            var settings = await db.SystemSettings
                .Where(s => keys.Contains(s.Key))
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

            if (!settings.TryGetValue(AccessTokenKey, out var encryptedAccess)
                || string.IsNullOrEmpty(encryptedAccess))
            {
                _logger.LogDebug("No persisted tokens found — waiting for browser login");
                return;
            }

            string accessToken;
            try
            {
                accessToken = _protector.Unprotect(encryptedAccess);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt persisted access token — data protection keys may have changed. Browser re-login required.");
                return;
            }

            string? refreshToken = null;
            if (settings.TryGetValue(RefreshTokenKey, out var encryptedRefresh)
                && !string.IsNullOrEmpty(encryptedRefresh))
            {
                try
                {
                    refreshToken = _protector.Unprotect(encryptedRefresh);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt persisted refresh token — proceeding with access token only");
                }
            }

            TimeSpan? expiresIn = null;
            if (settings.TryGetValue(ExpiresAtKey, out var expiresAtStr)
                && DateTimeOffset.TryParse(expiresAtStr, out var expiresAt))
            {
                var remaining = expiresAt - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                    expiresIn = remaining;
            }

            TimeSpan? refreshTokenExpiresIn = null;
            if (settings.TryGetValue(RefreshTokenExpiresAtKey, out var rtExpiresAtStr)
                && DateTimeOffset.TryParse(rtExpiresAtStr, out var rtExpiresAt))
            {
                var remaining = rtExpiresAt - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                    refreshTokenExpiresIn = remaining;
            }

            _isRestoring = true;
            try
            {
                _tokenProvider.SetTokens(accessToken, refreshToken, expiresIn, refreshTokenExpiresIn);
            }
            finally
            {
                _isRestoring = false;
            }

            _logger.LogInformation(
                "Restored tokens from database — access token {Status}, refresh token {RefreshStatus}",
                expiresIn.HasValue ? $"expires in {expiresIn.Value:hh\\:mm}" : "expired (will attempt refresh)",
                refreshToken is not null ? "available" : "not available");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore tokens from database — browser login may be required");
        }
    }

    internal async Task PersistTokensAsync()
    {
        await _persistLock.WaitAsync();
        try
        {
            // Snapshot all values before DB operations to avoid mixed state
            var accessToken = _tokenProvider.Token;
            var refreshToken = _tokenProvider.RefreshToken;
            var expiresAtUtc = _tokenProvider.ExpiresAtUtc;
            var refreshTokenExpiresAtUtc = _tokenProvider.RefreshTokenExpiresAtUtc;

            if (string.IsNullOrEmpty(accessToken))
                return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            var encrypted = _protector.Protect(accessToken);
            await UpsertSettingAsync(db, AccessTokenKey, encrypted);

            if (!string.IsNullOrEmpty(refreshToken))
            {
                var encryptedRefresh = _protector.Protect(refreshToken);
                await UpsertSettingAsync(db, RefreshTokenKey, encryptedRefresh);
            }
            else
            {
                await DeleteSettingAsync(db, RefreshTokenKey);
            }

            if (expiresAtUtc.HasValue)
                await UpsertSettingAsync(db, ExpiresAtKey, expiresAtUtc.Value.ToString("o"));
            else
                await DeleteSettingAsync(db, ExpiresAtKey);

            if (refreshTokenExpiresAtUtc.HasValue)
                await UpsertSettingAsync(db, RefreshTokenExpiresAtKey, refreshTokenExpiresAtUtc.Value.ToString("o"));
            else
                await DeleteSettingAsync(db, RefreshTokenExpiresAtKey);

            await db.SaveChangesAsync();

            _logger.LogDebug("Persisted tokens to database");
        }
        finally
        {
            _persistLock.Release();
        }
    }

    /// <summary>
    /// Removes all persisted token data from the database.
    /// Called on logout to ensure stale credentials don't survive a restart.
    /// </summary>
    internal async Task ClearPersistedTokensAsync()
    {
        await _persistLock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            var keys = new[] { AccessTokenKey, RefreshTokenKey, ExpiresAtKey, RefreshTokenExpiresAtKey };
            var settings = await db.SystemSettings
                .Where(s => keys.Contains(s.Key))
                .ToListAsync();

            if (settings.Count > 0)
            {
                db.SystemSettings.RemoveRange(settings);
                await db.SaveChangesAsync();
                _logger.LogInformation("Cleared persisted tokens from database");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear persisted tokens from database");
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private static async Task UpsertSettingAsync(AgentAcademyDbContext db, string key, string value)
    {
        var entity = await db.SystemSettings.FindAsync(key);
        if (entity is null)
        {
            db.SystemSettings.Add(new SystemSettingEntity
            {
                Key = key,
                Value = value,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            entity.Value = value;
            entity.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static async Task DeleteSettingAsync(AgentAcademyDbContext db, string key)
    {
        var entity = await db.SystemSettings.FindAsync(key);
        if (entity is not null)
            db.SystemSettings.Remove(entity);
    }
}
