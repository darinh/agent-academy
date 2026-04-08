using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public class NotificationDeliveryTrackerTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteConnection _connection;
    private readonly NotificationDeliveryTracker _tracker;

    public NotificationDeliveryTrackerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options =>
            options.UseSqlite(_connection));
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = Substitute.For<ILogger<NotificationDeliveryTracker>>();
        _tracker = new NotificationDeliveryTracker(scopeFactory, logger);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    #region RecordDeliveryAsync

    [Fact]
    public async Task RecordDeliveryAsync_PersistsDeliveryRecord()
    {
        await _tracker.RecordDeliveryAsync("Broadcast", "discord", "Test Title", "Body text", "room-1", "agent-1");

        var deliveries = await _tracker.GetDeliveriesAsync();
        var d = Assert.Single(deliveries);
        Assert.Equal("Broadcast", d.Channel);
        Assert.Equal("discord", d.ProviderId);
        Assert.Equal("Delivered", d.Status);
        Assert.Equal("Test Title", d.Title);
        Assert.Equal("Body text", d.Body);
        Assert.Equal("room-1", d.RoomId);
        Assert.Equal("agent-1", d.AgentId);
        Assert.Null(d.Error);
    }

    [Fact]
    public async Task RecordDeliveryAsync_TruncatesLongBody()
    {
        var longBody = new string('x', 1000);
        await _tracker.RecordDeliveryAsync("Broadcast", "discord", "Title", longBody, null, null);

        var deliveries = await _tracker.GetDeliveriesAsync();
        var d = Assert.Single(deliveries);
        Assert.Equal(500, d.Body!.Length);
    }

    [Fact]
    public async Task RecordDeliveryAsync_AllowsNullFields()
    {
        await _tracker.RecordDeliveryAsync("Broadcast", "console", null, null, null, null);

        var deliveries = await _tracker.GetDeliveriesAsync();
        var d = Assert.Single(deliveries);
        Assert.Null(d.Title);
        Assert.Null(d.Body);
        Assert.Null(d.RoomId);
        Assert.Null(d.AgentId);
    }

    #endregion

    #region RecordSkippedAsync

    [Fact]
    public async Task RecordSkippedAsync_PersistsWithSkippedStatus()
    {
        await _tracker.RecordSkippedAsync("Broadcast", "console", "Skipped Title", null, "room-1", null);

        var deliveries = await _tracker.GetDeliveriesAsync();
        var d = Assert.Single(deliveries);
        Assert.Equal("Skipped", d.Status);
    }

    #endregion

    #region RecordFailureAsync

    [Fact]
    public async Task RecordFailureAsync_PersistsWithErrorMessage()
    {
        await _tracker.RecordFailureAsync("AgentQuestion", "discord", "Question?", null, "room-1", "agent-1", "Connection refused");

        var deliveries = await _tracker.GetDeliveriesAsync();
        var d = Assert.Single(deliveries);
        Assert.Equal("Failed", d.Status);
        Assert.Equal("Connection refused", d.Error);
        Assert.Equal("AgentQuestion", d.Channel);
    }

    #endregion

    #region GetDeliveriesAsync — Filtering

    [Fact]
    public async Task GetDeliveriesAsync_FiltersBy_Channel()
    {
        await _tracker.RecordDeliveryAsync("Broadcast", "discord", "A", null, null, null);
        await _tracker.RecordDeliveryAsync("AgentQuestion", "discord", "B", null, null, null);

        var results = await _tracker.GetDeliveriesAsync(channel: "Broadcast");
        Assert.Single(results);
        Assert.Equal("A", results[0].Title);
    }

    [Fact]
    public async Task GetDeliveriesAsync_FiltersBy_ProviderId()
    {
        await _tracker.RecordDeliveryAsync("Broadcast", "discord", "D", null, null, null);
        await _tracker.RecordDeliveryAsync("Broadcast", "console", "C", null, null, null);

        var results = await _tracker.GetDeliveriesAsync(providerId: "console");
        Assert.Single(results);
        Assert.Equal("C", results[0].Title);
    }

    [Fact]
    public async Task GetDeliveriesAsync_FiltersBy_Status()
    {
        await _tracker.RecordDeliveryAsync("Broadcast", "discord", "OK", null, null, null);
        await _tracker.RecordFailureAsync("Broadcast", "discord", "FAIL", null, null, null, "err");

        var results = await _tracker.GetDeliveriesAsync(status: "Failed");
        Assert.Single(results);
        Assert.Equal("FAIL", results[0].Title);
    }

    [Fact]
    public async Task GetDeliveriesAsync_FiltersBy_RoomId()
    {
        await _tracker.RecordDeliveryAsync("Broadcast", "discord", "R1", null, "room-a", null);
        await _tracker.RecordDeliveryAsync("Broadcast", "discord", "R2", null, "room-b", null);

        var results = await _tracker.GetDeliveriesAsync(roomId: "room-a");
        Assert.Single(results);
        Assert.Equal("R1", results[0].Title);
    }

    [Fact]
    public async Task GetDeliveriesAsync_RespectsLimitAndOffset()
    {
        for (int i = 0; i < 10; i++)
            await _tracker.RecordDeliveryAsync("Broadcast", "discord", $"Item{i}", null, null, null);

        var page1 = await _tracker.GetDeliveriesAsync(limit: 3, offset: 0);
        Assert.Equal(3, page1.Count);

        var page2 = await _tracker.GetDeliveriesAsync(limit: 3, offset: 3);
        Assert.Equal(3, page2.Count);

        // No overlap
        Assert.Empty(page1.Select(d => d.Id).Intersect(page2.Select(d => d.Id)));
    }

    [Fact]
    public async Task GetDeliveriesAsync_ClampsLimit()
    {
        for (int i = 0; i < 5; i++)
            await _tracker.RecordDeliveryAsync("Broadcast", "discord", $"Item{i}", null, null, null);

        // Negative limit clamped to 1
        var results = await _tracker.GetDeliveriesAsync(limit: -10);
        Assert.Single(results);
    }

    [Fact]
    public async Task GetDeliveriesAsync_OrdersByAttemptedAtDescending()
    {
        await _tracker.RecordDeliveryAsync("Broadcast", "discord", "First", null, null, null);
        await Task.Delay(10); // Ensure different timestamps
        await _tracker.RecordDeliveryAsync("Broadcast", "discord", "Second", null, null, null);

        var results = await _tracker.GetDeliveriesAsync();
        Assert.Equal("Second", results[0].Title);
        Assert.Equal("First", results[1].Title);
    }

    #endregion

    #region GetDeliveryStatsAsync

    [Fact]
    public async Task GetDeliveryStatsAsync_GroupsByStatus()
    {
        await _tracker.RecordDeliveryAsync("Broadcast", "discord", "A", null, null, null);
        await _tracker.RecordDeliveryAsync("Broadcast", "console", "B", null, null, null);
        await _tracker.RecordFailureAsync("Broadcast", "slack", "C", null, null, null, "err");
        await _tracker.RecordSkippedAsync("Broadcast", "console", "D", null, null, null);

        var stats = await _tracker.GetDeliveryStatsAsync();
        Assert.Equal(2, stats["Delivered"]);
        Assert.Equal(1, stats["Failed"]);
        Assert.Equal(1, stats["Skipped"]);
    }

    [Fact]
    public async Task GetDeliveryStatsAsync_ReturnsEmpty_WhenNoDeliveries()
    {
        var stats = await _tracker.GetDeliveryStatsAsync();
        Assert.Empty(stats);
    }

    #endregion

    #region AllChannelTypes

    [Theory]
    [InlineData("Broadcast")]
    [InlineData("AgentQuestion")]
    [InlineData("DirectMessage")]
    [InlineData("RoomRenamed")]
    public async Task RecordDeliveryAsync_SupportsAllChannelTypes(string channel)
    {
        await _tracker.RecordDeliveryAsync(channel, "discord", "Title", null, null, null);

        var deliveries = await _tracker.GetDeliveriesAsync(channel: channel);
        Assert.Single(deliveries);
        Assert.Equal(channel, deliveries[0].Channel);
    }

    #endregion
}
