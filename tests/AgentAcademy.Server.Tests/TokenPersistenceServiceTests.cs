using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class TokenPersistenceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtectionProvider _dataProtection;

    public TokenPersistenceServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        var db = new AgentAcademyDbContext(options);
        db.Database.EnsureCreated();

        // Set up a real service scope factory with DB and data protection
        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(_connection));
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var provider = services.BuildServiceProvider();

        _scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        _dataProtection = provider.GetRequiredService<IDataProtectionProvider>();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private TokenPersistenceService CreateSut(CopilotTokenProvider tokenProvider) =>
        new(tokenProvider, _scopeFactory, _dataProtection,
            NullLogger<TokenPersistenceService>.Instance);

    [Fact]
    public async Task PersistAndRestore_RoundTrips_AllTokenData()
    {
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        // Set tokens and persist
        provider.SetTokens("access-123", "refresh-456", TimeSpan.FromHours(8), TimeSpan.FromDays(180));
        await sut.PersistTokensAsync();

        // Create a fresh provider and restore
        var provider2 = new CopilotTokenProvider();
        var sut2 = CreateSut(provider2);
        await sut2.RestoreTokensAsync();

        Assert.Equal("access-123", provider2.Token);
        Assert.Equal("refresh-456", provider2.RefreshToken);
        Assert.NotNull(provider2.ExpiresAtUtc);
        Assert.NotNull(provider2.RefreshTokenExpiresAtUtc);
    }

    [Fact]
    public async Task Restore_WhenNoPersistedTokens_DoesNothing()
    {
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        await sut.RestoreTokensAsync();

        Assert.Null(provider.Token);
        Assert.Null(provider.RefreshToken);
    }

    [Fact]
    public async Task Persist_EncryptsTokensInDatabase()
    {
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        provider.SetTokens("secret-access-token", "secret-refresh-token", TimeSpan.FromHours(8));
        await sut.PersistTokensAsync();

        // Read raw values from DB — they should NOT be the plaintext tokens
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var accessSetting = await db.SystemSettings.FindAsync("auth:accessToken");
        var refreshSetting = await db.SystemSettings.FindAsync("auth:refreshToken");

        Assert.NotNull(accessSetting);
        Assert.NotNull(refreshSetting);
        Assert.NotEqual("secret-access-token", accessSetting.Value);
        Assert.NotEqual("secret-refresh-token", refreshSetting.Value);
    }

    [Fact]
    public async Task TokenChanged_AutoPersists()
    {
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        await sut.StartAsync(CancellationToken.None);

        // Trigger a token change
        provider.SetTokens("auto-persist-token", "auto-persist-refresh", TimeSpan.FromHours(8));

        // Give the async handler time to complete
        await Task.Delay(200);

        // Create a fresh provider and verify persistence happened
        var provider2 = new CopilotTokenProvider();
        var sut2 = CreateSut(provider2);
        await sut2.RestoreTokensAsync();

        Assert.Equal("auto-persist-token", provider2.Token);
        Assert.Equal("auto-persist-refresh", provider2.RefreshToken);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Restore_DoesNotTriggerPersistLoop()
    {
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        // Persist initial tokens
        provider.SetTokens("initial-token", "initial-refresh", TimeSpan.FromHours(8));
        await sut.PersistTokensAsync();

        // Now start the service (which restores and subscribes)
        var provider2 = new CopilotTokenProvider();
        var sut2 = CreateSut(provider2);

        await sut2.StartAsync(CancellationToken.None);

        // The restore sets tokens via _isRestoring flag, so the OnTokenChanged
        // handler should skip the persist. Verify the token was restored.
        Assert.Equal("initial-token", provider2.Token);

        await sut2.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ClearPersistedTokens_RemovesAllTokenData()
    {
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        // Persist tokens
        provider.SetTokens("to-clear", "to-clear-refresh", TimeSpan.FromHours(8), TimeSpan.FromDays(180));
        await sut.PersistTokensAsync();

        // Clear persisted tokens
        await sut.ClearPersistedTokensAsync();

        // Restore should find nothing
        var provider2 = new CopilotTokenProvider();
        var sut2 = CreateSut(provider2);
        await sut2.RestoreTokensAsync();

        Assert.Null(provider2.Token);
        Assert.Null(provider2.RefreshToken);
    }

    [Fact]
    public async Task Persist_PreservesExistingRefreshTokenWhenNotSupplied()
    {
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        // First persist with full data
        provider.SetTokens("token-v1", "refresh-v1", TimeSpan.FromHours(8), TimeSpan.FromDays(180));
        await sut.PersistTokensAsync();

        // Update only access token — SetTokens preserves existing refresh
        provider.SetTokens("token-v2");
        await sut.PersistTokensAsync();

        // Restore should get v2 access token and preserved refresh-v1
        var provider2 = new CopilotTokenProvider();
        var sut2 = CreateSut(provider2);
        await sut2.RestoreTokensAsync();

        Assert.Equal("token-v2", provider2.Token);
        Assert.Equal("refresh-v1", provider2.RefreshToken);
    }

    [Fact]
    public async Task Persist_UpdatesExistingValues()
    {
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        // First persist
        provider.SetTokens("token-v1", "refresh-v1", TimeSpan.FromHours(8));
        await sut.PersistTokensAsync();

        // Second persist with updated values
        provider.SetTokens("token-v2", "refresh-v2", TimeSpan.FromHours(8));
        await sut.PersistTokensAsync();

        // Restore should get v2
        var provider2 = new CopilotTokenProvider();
        var sut2 = CreateSut(provider2);
        await sut2.RestoreTokensAsync();

        Assert.Equal("token-v2", provider2.Token);
        Assert.Equal("refresh-v2", provider2.RefreshToken);
    }

    [Fact]
    public async Task Persist_WithNoAccessTokenSet_SkipsDatabaseWrite()
    {
        // Kills: L171 statement-removal of `return;` guard in PersistTokensAsync.
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        // Provider has never had SetTokens called — Token is null.
        await sut.PersistTokensAsync();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    [Fact]
    public async Task Persist_WithoutRefreshToken_DeletesPersistedRefreshTokenRow()
    {
        // Kills: L186 statement-removal of DeleteSettingAsync(RefreshTokenKey) and
        //        L266 statement-removal of db.SystemSettings.Remove(entity) in DeleteSettingAsync.
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        // Seed DB with refresh token first.
        provider.SetTokens("access", "refresh", TimeSpan.FromHours(1));
        await sut.PersistTokensAsync();

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            Assert.NotNull(await db.SystemSettings.FindAsync("auth:refreshToken"));
        }

        // Now re-persist with a fresh provider that has access token but NO refresh token.
        var noRefreshProvider = new CopilotTokenProvider();
        noRefreshProvider.SetTokens("access-only", refreshToken: null, expiresIn: TimeSpan.FromHours(1));
        // SetTokens with null refresh preserves existing, so we bypass via clearing first.
        // Simpler path: persist through a provider whose RefreshToken property is null.
        var sut2 = CreateSut(noRefreshProvider);
        await sut2.PersistTokensAsync();

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            Assert.Null(await db.SystemSettings.FindAsync("auth:refreshToken"));
        }
    }

    [Fact]
    public async Task Persist_WithoutExpiries_DeletesPersistedExpiryRows()
    {
        // Kills: L192/L197/L200/L205 statement-removal of DeleteSettingAsync for expiry keys,
        // and L266 Remove(entity) in the executed delete path.
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        // First persist with expiries.
        provider.SetTokens("a", "r", TimeSpan.FromHours(1), TimeSpan.FromDays(1));
        await sut.PersistTokensAsync();

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            Assert.NotNull(await db.SystemSettings.FindAsync("auth:expiresAt"));
            Assert.NotNull(await db.SystemSettings.FindAsync("auth:refreshTokenExpiresAt"));
        }

        // Fresh provider with no expiries.
        var noExpiryProvider = new CopilotTokenProvider();
        noExpiryProvider.SetTokens("a", "r");
        var sut2 = CreateSut(noExpiryProvider);
        await sut2.PersistTokensAsync();

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            Assert.Null(await db.SystemSettings.FindAsync("auth:expiresAt"));
            Assert.Null(await db.SystemSettings.FindAsync("auth:refreshTokenExpiresAt"));
        }
    }

    [Fact]
    public async Task Persist_StoresExpiriesInRoundTrippableFormat()
    {
        // Kills: L219/L224 string-mutation of the "o" (round-trip) format specifier.
        // Mutating "o" → "" produces a culture-short DateTime string that loses the
        // fractional-second + offset structure. We assert on the raw serialized form:
        // the round-trip format always contains a literal 'T' separator, a fractional
        // '.' second marker, and a trailing 'Z' (or explicit offset).
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        provider.SetTokens("access", "refresh", TimeSpan.FromHours(5), TimeSpan.FromDays(30));
        await sut.PersistTokensAsync();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var expiresAtRow = await db.SystemSettings.FindAsync("auth:expiresAt");
        var rtExpiresAtRow = await db.SystemSettings.FindAsync("auth:refreshTokenExpiresAt");

        Assert.NotNull(expiresAtRow);
        Assert.NotNull(rtExpiresAtRow);

        // Round-trip format "o" produces e.g. "2026-04-17T01:45:28.4790000Z".
        // Culture-default ("" mutation) produces e.g. "4/17/2026 1:45:28 AM".
        foreach (var (value, label) in new[] { (expiresAtRow!.Value, "expiresAt"), (rtExpiresAtRow!.Value, "refreshTokenExpiresAt") })
        {
            Assert.True(value.Contains('T'), $"{label} missing 'T' date/time separator — \"o\" format specifier may have been mutated. Actual: {value}");
            Assert.True(value.Contains('.'), $"{label} missing fractional-second '.' — \"o\" format specifier may have been mutated. Actual: {value}");
        }
    }

    [Fact]
    public async Task Restore_ThenTokenChange_StillTriggersPersist()
    {
        // Kills: L167 boolean mutation `_isRestoring = false → true` in the Restore
        // finally block. Under mutation, _isRestoring remains true after Restore,
        // so every subsequent OnTokenChanged early-returns and no persist ever runs.
        var seedProvider = new CopilotTokenProvider();
        var seed = CreateSut(seedProvider);
        seedProvider.SetTokens("seed-access", "seed-refresh", TimeSpan.FromHours(1));
        await seed.PersistTokensAsync();

        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        // StartAsync -> Restore (which SHOULD reset _isRestoring=false in the finally),
        // then subscribes to TokenChanged.
        await sut.StartAsync(CancellationToken.None);
        Assert.Equal("seed-access", provider.Token);

        // Now cause a fresh token update — this must persist. If L167 mutated,
        // _isRestoring is stuck at true and OnTokenChanged no-ops.
        provider.SetTokens("post-restore-access", "post-restore-refresh", TimeSpan.FromHours(1));
        await Task.Delay(200);

        var verifyProvider = new CopilotTokenProvider();
        var verifySut = CreateSut(verifyProvider);
        await verifySut.RestoreTokensAsync();
        Assert.Equal("post-restore-access", verifyProvider.Token);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Restore_WithMissingAccessTokenRow_DoesNotAttemptDecryption()
    {
        // Kills: L101 logical `|| → &&` in the access-token guard AND
        //        L107 statement-removal of the `return;` inside the guard.
        // Both mutations cause the code to flow past the guard on an empty DB:
        //  - `&&` mutation: !TryGet=true && IsNullOrEmpty=true enters the if-block,
        //    but removing `return;` also means flow falls through. Either way the
        //    decrypt try/catch runs on null ciphertext, which logs
        //    "Failed to decrypt persisted access token". Under original code,
        //    the early-return fires FIRST and the decrypt catch never runs.
        var provider = new CopilotTokenProvider();
        var logger = new CapturingLogger<TokenPersistenceService>();
        var sut = new TokenPersistenceService(
            provider, _scopeFactory, _dataProtection, logger);

        // Empty DB — no access token row exists.
        await sut.RestoreTokensAsync();

        Assert.DoesNotContain(logger.Entries,
            e => e.Level == LogLevel.Warning
              && e.Message.Contains("Failed to decrypt persisted access token"));
        // Positive: the "no tokens" debug log DID fire (catches mutation that
        // removes the early-return message too).
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug
              && e.Message.Contains("No persisted tokens found"));
    }

    [Fact]
    public async Task Restore_WithEmptyAccessTokenRow_TreatsAsMissing()
    {
        // Secondary kill for L101 `|| → &&`: the access-token guard must early-return
        // when the row exists but its value is empty. Under `&&` mutation,
        // !TryGet=false && IsNullOrEmpty=true = false, so the early-return does NOT
        // fire and decrypt runs on "", which throws → "Failed to decrypt" warning.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.SystemSettings.Add(new SystemSettingEntity
            {
                Key = "auth:accessToken",
                Value = "",
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var provider = new CopilotTokenProvider();
        var logger = new CapturingLogger<TokenPersistenceService>();
        var sut = new TokenPersistenceService(
            provider, _scopeFactory, _dataProtection, logger);

        await sut.RestoreTokensAsync();

        Assert.DoesNotContain(logger.Entries,
            e => e.Level == LogLevel.Warning
              && e.Message.Contains("Failed to decrypt persisted access token"));
        Assert.Null(provider.Token);
    }

    [Fact]
    public async Task Restore_WithEmptyRefreshTokenRow_DoesNotAttemptRefreshDecryption()
    {
        // Kills: L131 logical mutation `&& → ||` in the refresh-token guard.
        // Under `||`, TryGet=true || !IsNullOrEmpty(empty)=false = true → enters and
        // calls Unprotect("") which throws → "Failed to decrypt persisted refresh
        // token" warning. Under original `&&`, the guard short-circuits and no
        // warning fires.
        var seedProvider = new CopilotTokenProvider();
        var seed = CreateSut(seedProvider);
        seedProvider.SetTokens("access", "refresh", TimeSpan.FromHours(1));
        await seed.PersistTokensAsync();

        // Force the refresh-token row to an empty string to exercise the guard edge.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var rt = await db.SystemSettings.FindAsync("auth:refreshToken");
            rt!.Value = "";
            await db.SaveChangesAsync();
        }

        var provider = new CopilotTokenProvider();
        var logger = new CapturingLogger<TokenPersistenceService>();
        var sut = new TokenPersistenceService(
            provider, _scopeFactory, _dataProtection, logger);

        await sut.RestoreTokensAsync();

        Assert.Equal("access", provider.Token);
        Assert.Null(provider.RefreshToken);
        Assert.DoesNotContain(logger.Entries,
            e => e.Level == LogLevel.Warning
              && e.Message.Contains("Failed to decrypt persisted refresh token"));
    }

    [Fact]
    public async Task Clear_WhenNoTokensPersisted_DoesNotCallSaveChanges()
    {
        // Kills: L226 equality mutation `settings.Count > 0` → `settings.Count >= 0`.
        // Under `>=`, Clear would call RemoveRange(empty)+SaveChanges+LogInformation even
        // when no rows exist. We detect the mutation via a captured logger: the info
        // message must NOT be emitted when there are no tokens to clear.
        var provider = new CopilotTokenProvider();
        var logger = new CapturingLogger<TokenPersistenceService>();
        var sut = new TokenPersistenceService(provider, _scopeFactory, _dataProtection, logger);

        await sut.ClearPersistedTokensAsync();

        Assert.DoesNotContain(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Cleared persisted tokens"));
    }

    [Fact]
    public async Task Clear_WhenTokensExist_EmitsClearedLog()
    {
        // Paired positive case for L226: when rows exist, the info log IS emitted.
        var provider = new CopilotTokenProvider();
        var logger = new CapturingLogger<TokenPersistenceService>();
        var sut = new TokenPersistenceService(provider, _scopeFactory, _dataProtection, logger);

        provider.SetTokens("a", "r", TimeSpan.FromHours(1));
        await sut.PersistTokensAsync();

        await sut.ClearPersistedTokensAsync();

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Cleared persisted tokens"));
    }

    [Fact]
    public async Task Clear_ReleasesLock_AllowingSubsequentCalls()
    {
        // Kills: L239 statement-removal of _persistLock.Release() in ClearPersistedTokensAsync.
        // If the release is removed, the second call blocks forever.
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        await sut.ClearPersistedTokensAsync();
        // Must complete within a reasonable window; if Release was mutated away,
        // the semaphore is still held by the first call and this hangs.
        var second = sut.ClearPersistedTokensAsync();
        var completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(second, completed);
    }

    [Fact]
    public async Task Stop_UnsubscribesFromTokenChanged()
    {
        // Kills: L51 assignment mutation `-= → +=` in StopAsync.
        // Under mutation, Stop ADDS a second subscription, so the next token
        // change causes TWO persists (one redundant). We can't easily count
        // persists, but we can verify unsubscribe by: Stop, dispose scope
        // factory, trigger change, and check nothing throws / no DB write
        // occurs via a new SUT.
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        // After Stop, the handler must be detached. We verify by seeding a
        // token that would otherwise persist and confirming the DB is empty.
        // (If `+=` mutation: Start subscribed once, Stop added a second sub,
        //  so the handler IS still attached → persist happens → DB has rows.)
        provider.SetTokens("should-not-persist", "rt", TimeSpan.FromHours(1));
        await Task.Delay(150);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    [Fact]
    public async Task Dispose_UnsubscribesAndMarksDisposed()
    {
        // Kills: L57 boolean mutation `_disposed = true → false`,
        //        L58 assignment mutation `-= → +=` in Dispose,
        //        L64 logical mutation `_isRestoring || _disposed → _isRestoring && _disposed`.
        // All three mutations have the same observable failure: after Dispose,
        // a token change must NOT trigger a persist.
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        await sut.StartAsync(CancellationToken.None);
        sut.Dispose();

        // Changing tokens after Dispose must be a no-op (no DB write, no throw).
        provider.SetTokens("after-dispose", "rt", TimeSpan.FromHours(1));
        await Task.Delay(150);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    [Fact]
    public async Task Restore_WhenAccessTokenDecryptionFails_DoesNotCallSetTokens()
    {
        // Kills: L103-initialization and the (now active) block-removal of `return;`
        // inside the decrypt-failure catch. With accessToken="" fallback, removing
        // the catch's `return;` would invoke SetTokens("", ...) — observable as
        // Token == "" in the provider.
        var provider = new CopilotTokenProvider();
        var sutA = CreateSut(provider);
        // Seed DB via one data protector.
        provider.SetTokens("real-token", "rt", TimeSpan.FromHours(1));
        await sutA.PersistTokensAsync();

        // Create a SECOND service with a DIFFERENT ephemeral data protector — its
        // Unprotect call will throw on the ciphertext written by sutA.
        var isolatedServices = new ServiceCollection();
        isolatedServices.AddDataProtection().UseEphemeralDataProtectionProvider();
        var altProtection = isolatedServices.BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();

        var victim = new CopilotTokenProvider();
        var sutB = new TokenPersistenceService(
            victim, _scopeFactory, altProtection, NullLogger<TokenPersistenceService>.Instance);

        await sutB.RestoreTokensAsync();

        // Decryption failed — SetTokens must NOT have been invoked, so Token stays null.
        Assert.Null(victim.Token);
        Assert.Null(victim.RefreshToken);
    }

    [Fact]
    public async Task Restore_WhenRefreshTokenDecryptionFails_StillRestoresAccessToken()
    {
        // Covers the refresh-token decrypt catch block AND L114 `refreshToken = null`
        // path. We rotate the refresh-token row to an unprotect-able value while
        // leaving the access-token row readable.
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);
        provider.SetTokens("ok-access", "will-be-corrupted", TimeSpan.FromHours(1));
        await sut.PersistTokensAsync();

        // Corrupt just the refresh-token ciphertext with a value that won't decrypt.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var rt = await db.SystemSettings.FindAsync("auth:refreshToken");
            rt!.Value = "not-a-valid-protected-payload";
            await db.SaveChangesAsync();
        }

        var victim = new CopilotTokenProvider();
        var sut2 = CreateSut(victim);
        await sut2.RestoreTokensAsync();

        Assert.Equal("ok-access", victim.Token);
        Assert.Null(victim.RefreshToken);
    }

    [Fact]
    public async Task Restore_DoesNotInvokePersistViaTokenChangedCallback()
    {
        // Kills: L64 logical mutation `_isRestoring || _disposed` → `_isRestoring && _disposed`.
        // Under the mutated `&&`, during restore (isRestoring=true, disposed=false) the
        // early-return no longer fires, so the TokenChanged callback inside SetTokens()
        // invokes PersistTokensAsync — observable as a "Persisted tokens to database"
        // debug log entry. Under the original `||`, the callback early-returns.
        var seedProvider = new CopilotTokenProvider();
        var seed = CreateSut(seedProvider);
        seedProvider.SetTokens("seeded", "rt", TimeSpan.FromHours(1));
        await seed.PersistTokensAsync();

        var restoreProvider = new CopilotTokenProvider();
        var logger = new CapturingLogger<TokenPersistenceService>();
        var sut = new TokenPersistenceService(restoreProvider, _scopeFactory, _dataProtection, logger);

        // Subscribe (StartAsync internally calls RestoreTokensAsync then subscribes;
        // the order matters — the seeded value is already in DB so restore will
        // fire SetTokens, which raises TokenChanged on the subscribed callback).
        await sut.StartAsync(CancellationToken.None);

        // StartAsync subscribes AFTER RestoreTokensAsync. To exercise the callback
        // during restore, call RestoreTokensAsync a second time now that the handler
        // is subscribed — it will re-trigger SetTokens → TokenChanged → OnTokenChanged.
        // Under `||` semantics: OnTokenChanged early-returns (_isRestoring=true),
        // no "Persisted" debug log. Under `&&` semantics: it proceeds and logs.
        logger.Entries.Clear();
        await sut.RestoreTokensAsync();
        await Task.Delay(100); // give the async void callback time to complete

        Assert.DoesNotContain(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("Persisted tokens to database"));

        await sut.StopAsync(CancellationToken.None);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
