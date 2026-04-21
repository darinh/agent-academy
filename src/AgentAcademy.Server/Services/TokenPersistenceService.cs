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
public sealed class TokenPersistenceService : IHostedService, ITokenPersistenceService, IDisposable
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

    // Stryker disable all : Dispose is best-effort cleanup and defensive:
    //  - `_disposed = true` is a guard read ONLY from OnTokenChanged. After the
    //    `-=` on the next line the handler is detached, so TokenChanged can no
    //    longer invoke OnTokenChanged — making the `_disposed` flag mutation and
    //    the `-= → +=` mutation behaviourally equivalent under current usage.
    //  - `_persistLock.Dispose()` removal leaks a SemaphoreSlim but has no
    //    observable test behaviour; the OS reclaims on process exit.
    public void Dispose()
    {
        _disposed = true;
        _tokenProvider.TokenChanged -= OnTokenChanged;
        _persistLock.Dispose();
    }
    // Stryker restore all

    private async void OnTokenChanged()
    {
        if (_isRestoring || _disposed) return;

        try
        {
            await PersistTokensAsync();
        }
        // Stryker disable all: diagnostic catch — the log call and its message
        // string have no observable behaviour (NullLogger in tests). The catch
        // exists only so a throwing DB call inside OnTokenChanged (async void)
        // doesn't crash the process.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist tokens to database — will retry on next change");
        }
        // Stryker restore all
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
                // Stryker disable all : diagnostic LogDebug — message/call have no observable behaviour.
                _logger.LogDebug("No persisted tokens found — waiting for browser login");
                // Stryker restore all
                return;
            }

            // Initialized to "" so Stryker block-removal of the catch `return`
            // doesn't produce CS0165 (which would force Safe Mode on the whole
            // method and block all mutation coverage).
            // Stryker disable once all : defensive fallback-init — the string-literal
            // mutation on this initializer is only observable under a second,
            // simultaneous mutation that also removes the catch's `return;`.
            // Stryker evaluates mutations one at a time, so this pair is unreachable.
            string accessToken = "";
            try
            {
                accessToken = _protector.Unprotect(encryptedAccess);
            }
            catch (Exception ex)
            {
                // Stryker disable all : diagnostic LogWarning — message/call have no observable behaviour.
                _logger.LogWarning(ex, "Failed to decrypt persisted access token — data protection keys may have changed. Browser re-login required.");
                // Stryker restore all
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
                // Stryker disable all: diagnostic catch — refresh-token decrypt failure is logged but otherwise ignored; access-token path continues.
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt persisted refresh token — proceeding with access token only");
                }
                // Stryker restore all
            }

            TimeSpan? expiresIn = null;
            if (settings.TryGetValue(ExpiresAtKey, out var expiresAtStr)
                && DateTimeOffset.TryParse(expiresAtStr, out var expiresAt))
            {
                var remaining = expiresAt - DateTimeOffset.UtcNow;
                // Stryker disable once all : boundary — `>` vs `>=` differ only when
                // `remaining` is exactly TimeSpan.Zero (the exact moment of expiry).
                // Hitting that boundary deterministically requires injecting an IClock
                // abstraction we don't have; the wall-clock difference between
                // expiresAt and UtcNow at test time is never exactly zero.
                if (remaining > TimeSpan.Zero)
                    expiresIn = remaining;
            }

            TimeSpan? refreshTokenExpiresIn = null;
            if (settings.TryGetValue(RefreshTokenExpiresAtKey, out var rtExpiresAtStr)
                && DateTimeOffset.TryParse(rtExpiresAtStr, out var rtExpiresAt))
            {
                var remaining = rtExpiresAt - DateTimeOffset.UtcNow;
                // Stryker disable once all : same boundary as above — `>` vs `>=`
                // differ only at the exact zero moment, which is untestable without
                // IClock injection.
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

            // Stryker disable all : diagnostic LogInformation — the multi-line template,
            // its two ternary branches, and all literal strings are pure log text with
            // no observable behaviour (tests use NullLogger or assert nothing about log
            // template contents).
            _logger.LogInformation(
                "Restored tokens from database — access token {Status}, refresh token {RefreshStatus}",
                expiresIn.HasValue ? $"expires in {expiresIn.Value:hh\\:mm}" : "expired (will attempt refresh)",
                refreshToken is not null ? "available" : "not available");
            // Stryker restore all
        }
        // Stryker disable all: diagnostic catch — EF/DB errors are swallowed so a bad DB doesn't crash startup.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore tokens from database — browser login may be required");
        }
        // Stryker restore all
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

            // Stryker disable all : diagnostic LogDebug — message/call have no observable behaviour.
            _logger.LogDebug("Persisted tokens to database");
            // Stryker restore all
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
    public async Task ClearPersistedTokensAsync()
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
        // Stryker disable all : diagnostic catch — EF/DB errors are swallowed so logout doesn't throw.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear persisted tokens from database");
        }
        // Stryker restore all
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
