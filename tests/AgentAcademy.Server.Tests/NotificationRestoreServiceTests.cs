using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Notifications.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="NotificationRestoreService"/>: startup config
/// restoration, decryption, provider connection, and error handling.
/// Uses in-memory SQLite, real ConfigEncryptionService, and mocked providers.
/// </summary>
public sealed class NotificationRestoreServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly INotificationManager _notificationManager;
    private readonly ConfigEncryptionService _encryption;

    public NotificationRestoreServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        services.AddDbContext<AgentAcademyDbContext>(opt =>
            opt.UseSqlite(_connection));

        services.AddDataProtection()
            .UseEphemeralDataProtectionProvider();

        services.AddSingleton<ConfigEncryptionService>();
        services.AddLogging();

        _notificationManager = Substitute.For<INotificationManager>();
        services.AddSingleton(_notificationManager);

        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
        }

        _encryption = _serviceProvider.GetRequiredService<ConfigEncryptionService>();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private AgentAcademyDbContext CreateDb()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    }

    private async Task SeedConfigAsync(string providerId, string key, string value)
    {
        using var db = CreateDb();
        db.NotificationConfigs.Add(new NotificationConfigEntity
        {
            ProviderId = providerId,
            Key = key,
            Value = value,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private INotificationProvider CreateMockProvider(string providerId, List<ConfigField>? fields = null)
    {
        var provider = Substitute.For<INotificationProvider>();
        provider.ProviderId.Returns(providerId);
        provider.GetConfigSchema().Returns(new ProviderConfigSchema(
            ProviderId: providerId,
            DisplayName: providerId,
            Description: $"Test {providerId} provider",
            Fields: fields ?? [
                new ConfigField("Token", "Token", "secret", true),
                new ConfigField("Channel", "Channel", "string", true)
            ]
        ));
        return provider;
    }

    private NotificationRestoreService CreateService()
    {
        return new NotificationRestoreService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _notificationManager,
            NullLogger<NotificationRestoreService>.Instance);
    }

    private async Task RunServiceAsync(CancellationToken cancellationToken = default)
    {
        var service = CreateService();
        await service.StartAsync(cancellationToken);
        await service.StopAsync(CancellationToken.None);
    }

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task NoSavedConfigs_NoProvidersRestored()
    {
        await RunServiceAsync();

        _notificationManager.DidNotReceive().GetProvider(Arg.Any<string>());
    }

    [Fact]
    public async Task ProviderNotRegistered_Skipped()
    {
        await SeedConfigAsync("unknown-provider", "Token", "abc");
        _notificationManager.GetProvider("unknown-provider").Returns((INotificationProvider?)null);

        await RunServiceAsync();

        // GetProvider was called but no configure/connect since provider is null
        _notificationManager.Received(1).GetProvider("unknown-provider");
    }

    [Fact]
    public async Task SuccessfulRestore_ConfiguresAndConnects()
    {
        var provider = CreateMockProvider("discord");
        _notificationManager.GetProvider("discord").Returns(provider);

        var encryptedToken = _encryption.Encrypt("my-secret-token");
        await SeedConfigAsync("discord", "Token", encryptedToken);
        await SeedConfigAsync("discord", "Channel", "12345");

        await RunServiceAsync();

        await provider.Received(1).ConfigureAsync(Arg.Is<Dictionary<string, string>>(d =>
            d["Token"] == "my-secret-token" && d["Channel"] == "12345"));
        await provider.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaintextSecrets_PassedThrough()
    {
        var provider = CreateMockProvider("discord");
        _notificationManager.GetProvider("discord").Returns(provider);

        // Plaintext (no ENC.v1: prefix) — TryDecrypt returns it as-is
        await SeedConfigAsync("discord", "Token", "plaintext-token");
        await SeedConfigAsync("discord", "Channel", "12345");

        await RunServiceAsync();

        await provider.Received(1).ConfigureAsync(Arg.Is<Dictionary<string, string>>(d =>
            d["Token"] == "plaintext-token"));
    }

    [Fact]
    public async Task DecryptionFails_ProviderSkipped()
    {
        var provider = CreateMockProvider("discord");
        _notificationManager.GetProvider("discord").Returns(provider);

        // Invalid encrypted value (valid prefix but garbage ciphertext)
        await SeedConfigAsync("discord", "Token", "ENC.v1:corrupted-garbage-data");
        await SeedConfigAsync("discord", "Channel", "12345");

        await RunServiceAsync();

        // Should not attempt to configure the provider
        await provider.DidNotReceive().ConfigureAsync(Arg.Any<Dictionary<string, string>>());
        await provider.DidNotReceive().ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectFails_OtherProvidersStillRestore()
    {
        var discord = CreateMockProvider("discord");
        discord.ConnectAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Connection failed"));
        _notificationManager.GetProvider("discord").Returns(discord);

        var slack = CreateMockProvider("slack", [
            new ConfigField("WebhookUrl", "Webhook URL", "string", true)
        ]);
        _notificationManager.GetProvider("slack").Returns(slack);

        await SeedConfigAsync("discord", "Token", "token-val");
        await SeedConfigAsync("discord", "Channel", "12345");
        await SeedConfigAsync("slack", "WebhookUrl", "https://hooks.slack.com/xxx");

        await RunServiceAsync();

        // Discord was configured but connect failed
        await discord.Received(1).ConfigureAsync(Arg.Any<Dictionary<string, string>>());
        await discord.Received(1).ConnectAsync(Arg.Any<CancellationToken>());

        // Slack was still restored successfully
        await slack.Received(1).ConfigureAsync(Arg.Is<Dictionary<string, string>>(d =>
            d["WebhookUrl"] == "https://hooks.slack.com/xxx"));
        await slack.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfigureFails_ProviderNotConnected()
    {
        var provider = CreateMockProvider("discord");
        provider.ConfigureAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ArgumentException("Bad config"));
        _notificationManager.GetProvider("discord").Returns(provider);

        await SeedConfigAsync("discord", "Token", "bad-token");
        await SeedConfigAsync("discord", "Channel", "12345");

        // Should not throw — error is caught and logged
        await RunServiceAsync();

        await provider.Received(1).ConfigureAsync(Arg.Any<Dictionary<string, string>>());
        await provider.DidNotReceive().ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MultipleConfigKeys_GroupedByProvider()
    {
        var provider = CreateMockProvider("discord");
        _notificationManager.GetProvider("discord").Returns(provider);

        await SeedConfigAsync("discord", "Token", "tok");
        await SeedConfigAsync("discord", "Channel", "12345");
        await SeedConfigAsync("discord", "Extra", "val");

        await RunServiceAsync();

        await provider.Received(1).ConfigureAsync(Arg.Is<Dictionary<string, string>>(d =>
            d.Count == 3 && d["Token"] == "tok" && d["Channel"] == "12345" && d["Extra"] == "val"));
    }

    [Fact]
    public async Task CancellationRespected()
    {
        var provider = CreateMockProvider("discord");
        _notificationManager.GetProvider("discord").Returns(provider);

        await SeedConfigAsync("discord", "Token", "tok");
        await SeedConfigAsync("discord", "Channel", "12345");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await RunServiceAsync(cts.Token);

        // Cancellation before provider loop — no configure/connect
        await provider.DidNotReceive().ConfigureAsync(Arg.Any<Dictionary<string, string>>());
    }
}
