using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

public class ActivityPublisherTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ActivityBroadcaster _bus;
    private readonly ActivityPublisher _sut;

    private const string RoomId = "room-1";

    private static AgentDefinition MakeAgent(string id = "agent-1", string name = "TestBot") =>
        new(id, name, "engineer", "A test agent", "Do things", null,
            [], [], AutoJoinDefaultRoom: false);

    private void EnsureRoom(string roomId = RoomId)
    {
        if (_db.Rooms.Find(roomId) is not null) return;
        _db.Rooms.Add(new RoomEntity
        {
            Id = roomId, Name = "Test Room",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    public ActivityPublisherTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection).Options;
        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
        _bus = new ActivityBroadcaster();
        _sut = new ActivityPublisher(_db, _bus);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // --- Publish ---

    [Fact]
    public void Publish_CreatesActivityEvent_WithCorrectProperties()
    {
        var evt = _sut.Publish(
            ActivityEventType.RoomCreated, "room-1", "actor-1", "task-1",
            "Room created", correlationId: "corr-1");

        Assert.Equal(ActivityEventType.RoomCreated, evt.Type);
        Assert.Equal("room-1", evt.RoomId);
        Assert.Equal("actor-1", evt.ActorId);
        Assert.Equal("task-1", evt.TaskId);
        Assert.Equal("Room created", evt.Message);
        Assert.Equal("corr-1", evt.CorrelationId);
        Assert.Equal(ActivitySeverity.Info, evt.Severity);
        Assert.False(string.IsNullOrEmpty(evt.Id));
        Assert.True(evt.OccurredAt <= DateTime.UtcNow);
        Assert.True(evt.OccurredAt > DateTime.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public void Publish_AddsEntityToDbChangeTracker()
    {
        _sut.Publish(ActivityEventType.AgentLoaded, "r1", "a1", null, "loaded");

        var entry = Assert.Single(_db.ChangeTracker.Entries<Data.Entities.ActivityEventEntity>());
        Assert.Equal(EntityState.Added, entry.State);
        Assert.Equal("loaded", entry.Entity.Message);
    }

    [Fact]
    public void Publish_BroadcastsEventToSubscribers()
    {
        ActivityEvent? received = null;
        _bus.Subscribe(e => received = e);

        var evt = _sut.Publish(ActivityEventType.AgentLoaded, null, null, null, "hello");

        Assert.NotNull(received);
        Assert.Equal(evt.Id, received.Id);
    }

    [Fact]
    public void Publish_DefaultSeverityIsInfo()
    {
        var evt = _sut.Publish(ActivityEventType.AgentLoaded, null, null, null, "msg");

        Assert.Equal(ActivitySeverity.Info, evt.Severity);
    }

    [Fact]
    public void Publish_CustomSeverityUsed()
    {
        var evt = _sut.Publish(
            ActivityEventType.AgentFinished, null, null, null, "error!",
            severity: ActivitySeverity.Error);

        Assert.Equal(ActivitySeverity.Error, evt.Severity);
    }

    [Fact]
    public void Publish_NullOptionalFields_StillWorks()
    {
        var evt = _sut.Publish(
            ActivityEventType.AgentLoaded, roomId: null, actorId: null,
            taskId: null, message: "minimal");

        Assert.Null(evt.RoomId);
        Assert.Null(evt.ActorId);
        Assert.Null(evt.TaskId);
        Assert.Null(evt.CorrelationId);
        Assert.Equal("minimal", evt.Message);
    }

    [Fact]
    public void Publish_WithCorrelationId_IncludedInEvent()
    {
        var evt = _sut.Publish(
            ActivityEventType.AgentLoaded, null, null, null, "msg",
            correlationId: "my-corr-id");

        Assert.Equal("my-corr-id", evt.CorrelationId);

        var entity = _db.ChangeTracker.Entries<Data.Entities.ActivityEventEntity>()
            .Single().Entity;
        Assert.Equal("my-corr-id", entity.CorrelationId);
    }

    [Fact]
    public void Publish_GeneratesUniqueIds()
    {
        var evt1 = _sut.Publish(ActivityEventType.AgentLoaded, null, null, null, "a");
        var evt2 = _sut.Publish(ActivityEventType.AgentLoaded, null, null, null, "b");

        Assert.NotEqual(evt1.Id, evt2.Id);
    }

    [Fact]
    public void Publish_EventTypePersistedCorrectly()
    {
        _sut.Publish(ActivityEventType.AgentThinking, null, null, null, "think");

        var entity = _db.ChangeTracker.Entries<Data.Entities.ActivityEventEntity>()
            .Single().Entity;
        Assert.Equal("AgentThinking", entity.Type);
    }

    // --- PublishThinkingAsync ---

    [Fact]
    public async Task PublishThinkingAsync_CreatesAgentThinkingEvent()
    {
        EnsureRoom();
        var agent = MakeAgent();

        await _sut.PublishThinkingAsync(agent, RoomId);

        var recent = _bus.GetRecentActivity();
        var evt = Assert.Single(recent);
        Assert.Equal(ActivityEventType.AgentThinking, evt.Type);
        Assert.Equal(agent.Id, evt.ActorId);
        Assert.Equal(RoomId, evt.RoomId);
    }

    [Fact]
    public async Task PublishThinkingAsync_MessageContainsAgentName()
    {
        EnsureRoom();
        var agent = MakeAgent(name: "Athena");

        await _sut.PublishThinkingAsync(agent, RoomId);

        var evt = Assert.Single(_bus.GetRecentActivity());
        Assert.Contains("Athena", evt.Message);
        Assert.Contains("thinking", evt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishThinkingAsync_SavesChanges()
    {
        EnsureRoom();
        var agent = MakeAgent();

        await _sut.PublishThinkingAsync(agent, RoomId);

        // After SaveChangesAsync, entity state should be Unchanged (persisted)
        var entry = Assert.Single(_db.ChangeTracker.Entries<Data.Entities.ActivityEventEntity>());
        Assert.Equal(EntityState.Unchanged, entry.State);

        // Also verify via a fresh query
        Assert.Single(_db.ActivityEvents.ToList());
    }

    // --- PublishFinishedAsync ---

    [Fact]
    public async Task PublishFinishedAsync_CreatesAgentFinishedEvent()
    {
        EnsureRoom("room-2");
        var agent = MakeAgent();

        await _sut.PublishFinishedAsync(agent, "room-2");

        var evt = Assert.Single(_bus.GetRecentActivity());
        Assert.Equal(ActivityEventType.AgentFinished, evt.Type);
        Assert.Equal(agent.Id, evt.ActorId);
        Assert.Equal("room-2", evt.RoomId);
    }

    [Fact]
    public async Task PublishFinishedAsync_MessageContainsAgentName()
    {
        EnsureRoom();
        var agent = MakeAgent(name: "Hermes");

        await _sut.PublishFinishedAsync(agent, RoomId);

        var evt = Assert.Single(_bus.GetRecentActivity());
        Assert.Contains("Hermes", evt.Message);
        Assert.Contains("finished", evt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishFinishedAsync_SavesChanges()
    {
        EnsureRoom();
        var agent = MakeAgent();

        await _sut.PublishFinishedAsync(agent, RoomId);

        var entry = Assert.Single(_db.ChangeTracker.Entries<Data.Entities.ActivityEventEntity>());
        Assert.Equal(EntityState.Unchanged, entry.State);
        Assert.Single(_db.ActivityEvents.ToList());
    }

    // --- GetRecentActivity ---

    [Fact]
    public void GetRecentActivity_DelegatesToBroadcaster()
    {
        _sut.Publish(ActivityEventType.AgentLoaded, null, null, null, "one");
        _sut.Publish(ActivityEventType.RoomCreated, null, null, null, "two");

        var recent = _sut.GetRecentActivity();

        Assert.Equal(2, recent.Count);
        Assert.Equal("one", recent[0].Message);
        Assert.Equal("two", recent[1].Message);
    }

    // --- Subscribe ---

    [Fact]
    public void Subscribe_DelegatesToBroadcaster()
    {
        var events = new List<ActivityEvent>();
        _sut.Subscribe(e => events.Add(e));

        _sut.Publish(ActivityEventType.AgentLoaded, null, null, null, "a");
        _sut.Publish(ActivityEventType.AgentFinished, null, null, null, "b");

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Subscribe_ReturnsWorkingUnsubscribeAction()
    {
        var events = new List<ActivityEvent>();
        var unsubscribe = _sut.Subscribe(e => events.Add(e));

        _sut.Publish(ActivityEventType.AgentLoaded, null, null, null, "before");
        unsubscribe();
        _sut.Publish(ActivityEventType.AgentLoaded, null, null, null, "after");

        Assert.Single(events);
        Assert.Equal("before", events[0].Message);
    }
}
