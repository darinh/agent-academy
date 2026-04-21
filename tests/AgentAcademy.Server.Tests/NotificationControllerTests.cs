using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class NotificationControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly NotificationManager _manager;
    private readonly NotificationDeliveryTracker _tracker;
    private readonly NotificationController _controller;

    public NotificationControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        // Build a scope factory backed by the in-memory DB
        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(_connection));
        var sp = services.BuildServiceProvider();

        _tracker = new NotificationDeliveryTracker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NotificationDeliveryTracker>.Instance);

        _manager = new NotificationManager(
            NullLogger<NotificationManager>.Instance, _tracker);

        var encryption = new ConfigEncryptionService(
            DataProtectionProvider.Create("agent-academy-tests"));

        _controller = new NotificationController(
            _manager, _tracker, encryption, _db,
            NullLogger<NotificationController>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private INotificationProvider CreateFakeProvider(
        string id = "test-provider", string name = "Test Provider",
        bool configured = false, bool connected = false)
    {
        var provider = Substitute.For<INotificationProvider>();
        provider.ProviderId.Returns(id);
        provider.DisplayName.Returns(name);
        provider.IsConfigured.Returns(configured);
        provider.IsConnected.Returns(connected);
        provider.GetConfigSchema().Returns(new ProviderConfigSchema(
            id, name, "Test provider description",
            [new ConfigField("token", "API Token", "secret", true, "Your API token")]));
        return provider;
    }

    // ── GetProviders ─────────────────────────────────────────────

    [Fact]
    public void GetProviders_NoProviders_ReturnsEmptyList()
    {
        var result = _controller.GetProviders();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var providers = Assert.IsType<List<ProviderStatusDto>>(ok.Value);
        Assert.Empty(providers);
    }

    [Fact]
    public void GetProviders_WithProvider_ReturnsStatus()
    {
        var provider = CreateFakeProvider(configured: true, connected: false);
        _manager.RegisterProvider(provider);

        var result = _controller.GetProviders();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var providers = Assert.IsType<List<ProviderStatusDto>>(ok.Value);

        Assert.Single(providers);
        Assert.Equal("test-provider", providers[0].ProviderId);
        Assert.True(providers[0].IsConfigured);
        Assert.False(providers[0].IsConnected);
    }

    // ── GetSchema ────────────────────────────────────────────────

    [Fact]
    public void GetSchema_UnknownProvider_ReturnsNotFound()
    {
        var result = _controller.GetSchema("missing");
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public void GetSchema_KnownProvider_ReturnsSchema()
    {
        var provider = CreateFakeProvider();
        _manager.RegisterProvider(provider);

        var result = _controller.GetSchema("test-provider");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var schema = Assert.IsType<ProviderConfigSchema>(ok.Value);
        Assert.Equal("test-provider", schema.ProviderId);
        Assert.Single(schema.Fields);
        Assert.Equal("token", schema.Fields[0].Key);
    }

    // ── Configure ────────────────────────────────────────────────

    [Fact]
    public async Task Configure_NullConfig_ReturnsBadRequest()
    {
        var provider = CreateFakeProvider();
        _manager.RegisterProvider(provider);

        var result = await _controller.Configure("test-provider", null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Configure_UnknownProvider_ReturnsNotFound()
    {
        var result = await _controller.Configure("missing",
            new Dictionary<string, string> { ["key"] = "val" }, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Configure_Valid_ReturnsOkAndCallsProvider()
    {
        var provider = CreateFakeProvider();
        _manager.RegisterProvider(provider);

        var config = new Dictionary<string, string> { ["token"] = "abc123" };
        var result = await _controller.Configure("test-provider", config, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        await provider.Received(1).ConfigureAsync(
            Arg.Is<Dictionary<string, string>>(d => d["token"] == "abc123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Configure_OmittedKey_IsDeletedFromStorage()
    {
        // Regression: previously Configure only inserted/updated keys present in the
        // payload. A key removed from a subsequent payload (e.g. operator clears an old
        // webhook URL or rotates away from a secondary token) would silently persist in
        // the database and resurrect on the next restart through NotificationRestoreService.
        // Configure has full-replace semantics — keys absent from the payload must be
        // removed in the same transaction.
        var provider = Substitute.For<INotificationProvider>();
        provider.ProviderId.Returns("multi-key-provider");
        provider.DisplayName.Returns("Multi");
        provider.IsConfigured.Returns(false);
        provider.IsConnected.Returns(false);
        provider.GetConfigSchema().Returns(new ProviderConfigSchema(
            "multi-key-provider", "Multi", "desc",
            [
                new ConfigField("webhook_url", "Webhook", "string", false, ""),
                new ConfigField("token", "Token", "secret", false, "")
            ]));
        _manager.RegisterProvider(provider);

        // Initial configuration with both keys.
        var initial = new Dictionary<string, string>
        {
            ["webhook_url"] = "https://old.example.com/hook",
            ["token"] = "old-secret"
        };
        var first = await _controller.Configure("multi-key-provider", initial, CancellationToken.None);
        Assert.IsType<OkObjectResult>(first);

        var afterFirst = await _db.NotificationConfigs
            .Where(c => c.ProviderId == "multi-key-provider")
            .Select(c => c.Key)
            .OrderBy(k => k)
            .ToListAsync();
        Assert.Equal(new[] { "token", "webhook_url" }, afterFirst);

        // Reconfigure with only the token — the operator removed the webhook_url.
        var followup = new Dictionary<string, string>
        {
            ["token"] = "new-secret"
        };
        var second = await _controller.Configure("multi-key-provider", followup, CancellationToken.None);
        Assert.IsType<OkObjectResult>(second);

        var afterSecond = await _db.NotificationConfigs
            .Where(c => c.ProviderId == "multi-key-provider")
            .Select(c => c.Key)
            .OrderBy(k => k)
            .ToListAsync();
        Assert.Equal(new[] { "token" }, afterSecond);
    }

    [Fact]
    public async Task Configure_DoesNotDeleteOtherProviderKeys()
    {
        // Defence-in-depth for the stale-key fix: deleting absent keys must be
        // scoped to the provider being configured. A second provider's keys must
        // remain untouched.
        var providerA = CreateFakeProvider(id: "provider-a", name: "A");
        var providerB = CreateFakeProvider(id: "provider-b", name: "B");
        _manager.RegisterProvider(providerA);
        _manager.RegisterProvider(providerB);

        await _controller.Configure("provider-a",
            new Dictionary<string, string> { ["token"] = "a-tok" }, CancellationToken.None);
        await _controller.Configure("provider-b",
            new Dictionary<string, string> { ["token"] = "b-tok" }, CancellationToken.None);

        // Reconfigure A with no token at all — its keys go to zero, B is untouched.
        await _controller.Configure("provider-a",
            new Dictionary<string, string>(), CancellationToken.None);

        var aKeys = await _db.NotificationConfigs.Where(c => c.ProviderId == "provider-a").CountAsync();
        var bKeys = await _db.NotificationConfigs.Where(c => c.ProviderId == "provider-b").CountAsync();
        Assert.Equal(0, aKeys);
        Assert.Equal(1, bKeys);
    }

    // ── Connect ──────────────────────────────────────────────────

    [Fact]
    public async Task Connect_UnknownProvider_ReturnsNotFound()
    {
        var result = await _controller.Connect("missing", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Connect_Valid_ReturnsOkAndCallsConnect()
    {
        var provider = CreateFakeProvider(configured: true);
        _manager.RegisterProvider(provider);

        var result = await _controller.Connect("test-provider", CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        await provider.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Connect_ProviderThrows_Returns500()
    {
        var provider = CreateFakeProvider();
        provider.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Connection failed")));
        _manager.RegisterProvider(provider);

        var result = await _controller.Connect("test-provider", CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, status.StatusCode);
    }

    // ── Disconnect ───────────────────────────────────────────────

    [Fact]
    public async Task Disconnect_UnknownProvider_ReturnsNotFound()
    {
        var result = await _controller.Disconnect("missing", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Disconnect_Valid_ReturnsOkAndCallsDisconnect()
    {
        var provider = CreateFakeProvider(connected: true);
        _manager.RegisterProvider(provider);

        var result = await _controller.Disconnect("test-provider", CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        await provider.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
    }

    // ── SendTestNotification ─────────────────────────────────────

    [Fact]
    public async Task SendTestNotification_NoProviders_ReturnsSentZero()
    {
        var result = await _controller.SendTestNotification(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"sent\":0", json);
    }

    [Fact]
    public async Task SendTestNotification_ConnectedProvider_Sends()
    {
        var provider = CreateFakeProvider(connected: true);
        provider.IsConnected.Returns(true);
        provider.SendNotificationAsync(Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _manager.RegisterProvider(provider);

        var result = await _controller.SendTestNotification(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"sent\":1", json);
    }

    // ── GetDeliveries ────────────────────────────────────────────

    [Fact]
    public async Task GetDeliveries_EmptyDb_ReturnsEmptyList()
    {
        var result = await _controller.GetDeliveries();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    // ── GetDeliveryStats ─────────────────────────────────────────

    [Fact]
    public async Task GetDeliveryStats_ReturnsOk()
    {
        var result = await _controller.GetDeliveryStats(hours: 24);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }
}
