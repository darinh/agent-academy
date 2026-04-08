using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task Restore_WithExpiredAccessToken_StillRestoresForRefresh()
    {
        var provider = new CopilotTokenProvider();
        var sut = CreateSut(provider);

        // Persist with a very short expiry that will have passed
        provider.SetTokens("expired-token", "still-valid-refresh", TimeSpan.FromMilliseconds(1), TimeSpan.FromDays(180));
        await sut.PersistTokensAsync();

        // Wait for the access token to expire
        await Task.Delay(50);

        // Restore — the token should still be there so the monitor can refresh it
        var provider2 = new CopilotTokenProvider();
        var sut2 = CreateSut(provider2);
        await sut2.RestoreTokensAsync();

        Assert.Equal("expired-token", provider2.Token);
        Assert.Equal("still-valid-refresh", provider2.RefreshToken);
    }
}
