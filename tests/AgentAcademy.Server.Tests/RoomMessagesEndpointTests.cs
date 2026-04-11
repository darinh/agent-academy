using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class RoomMessagesEndpointTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly WorkspaceRuntime _runtime;
    private readonly AgentCatalogOptions _catalog;
    private readonly string _roomId = "test-room";

    public RoomMessagesEndpointTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        var catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents: []);
        _catalog = catalog;

        var logger = Substitute.For<ILogger<WorkspaceRuntime>>();
        var activityBus = new ActivityBroadcaster();
        var activityPublisher = new ActivityPublisher(_db, activityBus);
        var settingsService = new SystemSettingsService(_db);
        var executor = Substitute.For<IAgentExecutor>();
        var sessionLogger = Substitute.For<ILogger<ConversationSessionService>>();
        var sessionService = new ConversationSessionService(_db, settingsService, executor, sessionLogger);
        var taskQueries = new TaskQueryService(_db, NullLogger<TaskQueryService>.Instance, catalog);
        var taskLifecycle = new TaskLifecycleService(_db, NullLogger<TaskLifecycleService>.Instance, catalog, activityPublisher);
        var agentLocations = new AgentLocationService(_db, catalog, activityPublisher);
        _runtime = new WorkspaceRuntime(_db, logger, catalog, activityPublisher, sessionService, taskQueries, taskLifecycle,
            new MessageService(_db, NullLogger<MessageService>.Instance, catalog, activityPublisher, sessionService),
            new BreakoutRoomService(_db, NullLogger<BreakoutRoomService>.Instance, catalog, activityPublisher, sessionService, taskQueries, agentLocations),
            new TaskItemService(_db, NullLogger<TaskItemService>.Instance),
            new RoomService(_db, NullLogger<RoomService>.Instance, catalog, activityPublisher, sessionService,
                new MessageService(_db, NullLogger<MessageService>.Instance, catalog, activityPublisher, sessionService)),
            agentLocations);

        // Seed a room
        _db.Rooms.Add(new RoomEntity
        {
            Id = _roomId,
            Name = "Test Room",
            Status = "Active",
            CurrentPhase = "Discussion",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetRoomMessages_ReturnsEmptyForEmptyRoom()
    {
        var (messages, hasMore) = await _runtime.GetRoomMessagesAsync(_roomId);

        Assert.Empty(messages);
        Assert.False(hasMore);
    }

    [Fact]
    public async Task GetRoomMessages_ReturnsMessagesChronologically()
    {
        var baseTime = DateTime.UtcNow;
        SeedMessages(
            ("m1", "Hello", baseTime.AddSeconds(1)),
            ("m2", "World", baseTime.AddSeconds(2)),
            ("m3", "!", baseTime.AddSeconds(3)));

        var (messages, hasMore) = await _runtime.GetRoomMessagesAsync(_roomId);

        Assert.Equal(3, messages.Count);
        Assert.Equal("m1", messages[0].Id);
        Assert.Equal("m2", messages[1].Id);
        Assert.Equal("m3", messages[2].Id);
        Assert.False(hasMore);
    }

    [Fact]
    public async Task GetRoomMessages_CursorReturnsMessagesAfterCursor()
    {
        var baseTime = DateTime.UtcNow;
        SeedMessages(
            ("m1", "A", baseTime.AddSeconds(1)),
            ("m2", "B", baseTime.AddSeconds(2)),
            ("m3", "C", baseTime.AddSeconds(3)),
            ("m4", "D", baseTime.AddSeconds(4)));

        var (messages, hasMore) = await _runtime.GetRoomMessagesAsync(_roomId, afterMessageId: "m2");

        Assert.Equal(2, messages.Count);
        Assert.Equal("m3", messages[0].Id);
        Assert.Equal("m4", messages[1].Id);
        Assert.False(hasMore);
    }

    [Fact]
    public async Task GetRoomMessages_RespectsLimit()
    {
        var baseTime = DateTime.UtcNow;
        SeedMessages(
            ("m1", "A", baseTime.AddSeconds(1)),
            ("m2", "B", baseTime.AddSeconds(2)),
            ("m3", "C", baseTime.AddSeconds(3)),
            ("m4", "D", baseTime.AddSeconds(4)),
            ("m5", "E", baseTime.AddSeconds(5)));

        var (messages, hasMore) = await _runtime.GetRoomMessagesAsync(_roomId, limit: 3);

        Assert.Equal(3, messages.Count);
        Assert.Equal("m1", messages[0].Id);
        Assert.Equal("m2", messages[1].Id);
        Assert.Equal("m3", messages[2].Id);
        Assert.True(hasMore);
    }

    [Fact]
    public async Task GetRoomMessages_CursorWithLimit()
    {
        var baseTime = DateTime.UtcNow;
        SeedMessages(
            ("m1", "A", baseTime.AddSeconds(1)),
            ("m2", "B", baseTime.AddSeconds(2)),
            ("m3", "C", baseTime.AddSeconds(3)),
            ("m4", "D", baseTime.AddSeconds(4)),
            ("m5", "E", baseTime.AddSeconds(5)));

        var (messages, hasMore) = await _runtime.GetRoomMessagesAsync(_roomId, afterMessageId: "m1", limit: 2);

        Assert.Equal(2, messages.Count);
        Assert.Equal("m2", messages[0].Id);
        Assert.Equal("m3", messages[1].Id);
        Assert.True(hasMore);
    }

    [Fact]
    public async Task GetRoomMessages_ExcludesDmMessages()
    {
        var baseTime = DateTime.UtcNow;
        SeedMessages(("m1", "Public", baseTime.AddSeconds(1)));

        // Add a DM message (has RecipientId)
        _db.Messages.Add(new MessageEntity
        {
            Id = "dm1",
            RoomId = _roomId,
            SenderId = "human",
            SenderName = "Human",
            SenderKind = "User",
            Kind = "Response",
            Content = "Private DM",
            SentAt = baseTime.AddSeconds(2),
            RecipientId = "agent-1"
        });
        _db.SaveChanges();

        var (messages, _) = await _runtime.GetRoomMessagesAsync(_roomId);

        Assert.Single(messages);
        Assert.Equal("m1", messages[0].Id);
    }

    [Fact]
    public async Task GetRoomMessages_NonExistentRoom_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _runtime.GetRoomMessagesAsync("nonexistent-room"));
    }

    [Fact]
    public async Task GetRoomMessages_LimitClampedToMax200()
    {
        // Requesting limit > 200 should be clamped
        var baseTime = DateTime.UtcNow;
        SeedMessages(("m1", "A", baseTime));

        // Should not throw — limit is clamped internally
        var (messages, _) = await _runtime.GetRoomMessagesAsync(_roomId, limit: 500);

        Assert.Single(messages);
    }

    [Fact]
    public async Task GetRoomMessages_NonExistentCursor_ReturnsAllMessages()
    {
        var baseTime = DateTime.UtcNow;
        SeedMessages(
            ("m1", "A", baseTime.AddSeconds(1)),
            ("m2", "B", baseTime.AddSeconds(2)));

        // Cursor points to a message that doesn't exist — returns all messages
        var (messages, _) = await _runtime.GetRoomMessagesAsync(_roomId, afterMessageId: "nonexistent");

        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task Controller_GetRoomMessages_Returns200()
    {
        var baseTime = DateTime.UtcNow;
        SeedMessages(("m1", "Hello", baseTime));

        var controller = CreateController();

        var result = await controller.GetRoomMessages(_roomId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<RoomMessagesResponse>(ok.Value);
        Assert.Single(response.Messages);
        Assert.False(response.HasMore);
    }

    [Fact]
    public async Task Controller_GetRoomMessages_NonExistentRoom_Returns404()
    {
        var controller = CreateController();

        var result = await controller.GetRoomMessages("nonexistent");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Controller_GetRoomMessages_WithCursorAndLimit()
    {
        var baseTime = DateTime.UtcNow;
        SeedMessages(
            ("m1", "A", baseTime.AddSeconds(1)),
            ("m2", "B", baseTime.AddSeconds(2)),
            ("m3", "C", baseTime.AddSeconds(3)));

        var controller = CreateController();

        var result = await controller.GetRoomMessages(_roomId, after: "m1", limit: 1);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<RoomMessagesResponse>(ok.Value);
        Assert.Single(response.Messages);
        Assert.Equal("m2", response.Messages[0].Id);
        Assert.True(response.HasMore);
    }

    private RoomController CreateController()
    {
        var logger = Substitute.For<ILogger<RoomController>>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var usageTracker = new LlmUsageTracker(scopeFactory, NullLogger<LlmUsageTracker>.Instance);
        var errorTracker = new AgentErrorTracker(scopeFactory, NullLogger<AgentErrorTracker>.Instance);
        return new RoomController(_runtime, _catalog, usageTracker, errorTracker, logger);
    }

    private void SeedMessages(params (string id, string content, DateTime sentAt)[] messages)
    {
        foreach (var (id, content, sentAt) in messages)
        {
            _db.Messages.Add(new MessageEntity
            {
                Id = id,
                RoomId = _roomId,
                SenderId = "human",
                SenderName = "Human",
                SenderKind = "User",
                Kind = "Response",
                Content = content,
                SentAt = sentAt
            });
        }
        _db.SaveChanges();
    }
}
