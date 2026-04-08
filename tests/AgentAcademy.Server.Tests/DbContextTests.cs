using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Verifies the EF Core schema and basic CRUD against an in-memory SQLite database.
/// </summary>
public class DbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;

    public DbContextTests()
    {
        // Use a shared in-memory SQLite connection that stays open for the test lifetime
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Schema_CreatesAllTables()
    {
        // Verify all expected tables exist by querying sqlite_master
        var tables = _db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != '__EFMigrationsHistory' ORDER BY name")
            .ToList();

        Assert.Contains("rooms", tables);
        Assert.Contains("messages", tables);
        Assert.Contains("tasks", tables);
        Assert.Contains("task_items", tables);
        Assert.Contains("agent_locations", tables);
        Assert.Contains("breakout_rooms", tables);
        Assert.Contains("breakout_messages", tables);
        Assert.Contains("plans", tables);
        Assert.Contains("activity_events", tables);
    }

    [Fact]
    public void CanInsertAndQueryRoom()
    {
        var room = new RoomEntity
        {
            Id = "room-1",
            Name = "Test Room",
            Status = "Active",
            CurrentPhase = "Planning",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Rooms.Add(room);
        _db.SaveChanges();

        var loaded = _db.Rooms.Find("room-1");
        Assert.NotNull(loaded);
        Assert.Equal("Test Room", loaded.Name);
        Assert.Equal("Active", loaded.Status);
    }

    [Fact]
    public void CanInsertMessageWithRoomNavigation()
    {
        var room = new RoomEntity
        {
            Id = "room-msg",
            Name = "Msg Room",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Rooms.Add(room);

        var message = new MessageEntity
        {
            Id = "msg-1",
            RoomId = "room-msg",
            SenderId = "agent-1",
            SenderName = "Architect",
            SenderKind = "Agent",
            Kind = "Response",
            Content = "Hello world",
            SentAt = DateTime.UtcNow
        };
        _db.Messages.Add(message);
        _db.SaveChanges();

        var loaded = _db.Messages
            .Include(m => m.Room)
            .First(m => m.Id == "msg-1");

        Assert.Equal("room-msg", loaded.Room.Id);
        Assert.Equal("Hello world", loaded.Content);
    }

    [Fact]
    public void CanInsertTaskWithItems()
    {
        var room = new RoomEntity
        {
            Id = "room-task",
            Name = "Task Room",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Rooms.Add(room);

        var task = new TaskEntity
        {
            Id = "task-1",
            Title = "Build feature",
            RoomId = "room-task",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Tasks.Add(task);

        var item = new TaskItemEntity
        {
            Id = "item-1",
            Title = "Write tests",
            AssignedTo = "agent-2",
            RoomId = "room-task",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.TaskItems.Add(item);
        _db.SaveChanges();

        Assert.Equal(1, _db.Tasks.Count());
        Assert.Equal(1, _db.TaskItems.Count());
    }

    [Fact]
    public void CanInsertBreakoutRoomWithMessages()
    {
        var room = new RoomEntity
        {
            Id = "room-br",
            Name = "Parent",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Rooms.Add(room);

        var breakout = new BreakoutRoomEntity
        {
            Id = "br-1",
            Name = "Agent Breakout",
            ParentRoomId = "room-br",
            AssignedAgentId = "agent-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.BreakoutRooms.Add(breakout);

        var brMsg = new BreakoutMessageEntity
        {
            Id = "brmsg-1",
            BreakoutRoomId = "br-1",
            SenderId = "agent-1",
            SenderName = "Architect",
            SenderKind = "Agent",
            Kind = "Response",
            Content = "Working on it",
            SentAt = DateTime.UtcNow
        };
        _db.BreakoutMessages.Add(brMsg);
        _db.SaveChanges();

        var loaded = _db.BreakoutRooms
            .Include(br => br.Messages)
            .Include(br => br.ParentRoom)
            .First(br => br.Id == "br-1");

        Assert.Single(loaded.Messages);
        Assert.Equal("room-br", loaded.ParentRoom.Id);
    }

    [Fact]
    public void CanInsertPlanForRoomId()
    {
        var room = new RoomEntity
        {
            Id = "room-plan",
            Name = "Plan Room",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Rooms.Add(room);

        var plan = new PlanEntity
        {
            RoomId = "room-plan",
            Content = "Step 1: Design\nStep 2: Build",
            UpdatedAt = DateTime.UtcNow
        };
        _db.Plans.Add(plan);
        _db.SaveChanges();

        var loaded = _db.Plans.First(p => p.RoomId == "room-plan");

        Assert.Contains("Step 1", loaded.Content);
    }

    [Fact]
    public void CanInsertPlanForBreakoutRoomId()
    {
        var room = new RoomEntity
        {
            Id = "room-parent",
            Name = "Parent Room",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Rooms.Add(room);

        var breakout = new BreakoutRoomEntity
        {
            Id = "breakout-plan",
            Name = "BR: Plan Room",
            ParentRoomId = "room-parent",
            AssignedAgentId = "agent-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.BreakoutRooms.Add(breakout);

        var plan = new PlanEntity
        {
            RoomId = "breakout-plan",
            Content = "# Breakout Plan",
            UpdatedAt = DateTime.UtcNow
        };
        _db.Plans.Add(plan);
        _db.SaveChanges();

        var loaded = _db.Plans.First(p => p.RoomId == "breakout-plan");

        Assert.Equal("# Breakout Plan", loaded.Content);
    }

    [Fact]
    public void CanInsertAgentLocation()
    {
        var location = new AgentLocationEntity
        {
            AgentId = "agent-1",
            RoomId = "room-1",
            State = "Working",
            UpdatedAt = DateTime.UtcNow
        };
        _db.AgentLocations.Add(location);
        _db.SaveChanges();

        var loaded = _db.AgentLocations.Find("agent-1");
        Assert.NotNull(loaded);
        Assert.Equal("Working", loaded.State);
    }

    [Fact]
    public void CanInsertActivityEvent()
    {
        var room = new RoomEntity
        {
            Id = "room-ev",
            Name = "Event Room",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Rooms.Add(room);

        var ev = new ActivityEventEntity
        {
            Id = "ev-1",
            Type = "TaskCreated",
            Severity = "Info",
            RoomId = "room-ev",
            Message = "Task created",
            OccurredAt = DateTime.UtcNow
        };
        _db.ActivityEvents.Add(ev);
        _db.SaveChanges();

        var loaded = _db.ActivityEvents
            .Include(e => e.Room)
            .First(e => e.Id == "ev-1");

        Assert.Equal("room-ev", loaded.Room!.Id);
    }

    [Fact]
    public void Schema_HasExpectedIndexes()
    {
        // Verify key indexes exist
        var indexes = _db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='index' AND name LIKE 'idx_%' ORDER BY name")
            .ToList();

        Assert.Contains("idx_messages_room", indexes);
        Assert.Contains("idx_messages_sentAt", indexes);
        Assert.Contains("idx_tasks_room", indexes);
        Assert.Contains("idx_task_items_agent", indexes);
        Assert.Contains("idx_task_items_room", indexes);
        Assert.Contains("idx_breakout_rooms_parent", indexes);
        Assert.Contains("idx_activity_room", indexes);
        Assert.Contains("idx_activity_time", indexes);
    }

    [Fact]
    public void Schema_CreatesNotificationConfigsTable()
    {
        var tables = _db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != '__EFMigrationsHistory' ORDER BY name")
            .ToList();

        Assert.Contains("notification_configs", tables);
    }

    [Fact]
    public void CanInsertAndQueryNotificationConfig()
    {
        _db.NotificationConfigs.Add(new NotificationConfigEntity
        {
            ProviderId = "discord",
            Key = "bot_token",
            Value = "test-token-value",
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        var loaded = _db.NotificationConfigs
            .FirstOrDefault(c => c.ProviderId == "discord" && c.Key == "bot_token");

        Assert.NotNull(loaded);
        Assert.Equal("test-token-value", loaded.Value);
    }

    [Fact]
    public void NotificationConfig_EnforcesUniqueProviderKey()
    {
        _db.NotificationConfigs.Add(new NotificationConfigEntity
        {
            ProviderId = "discord",
            Key = "channel_id",
            Value = "123",
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        _db.NotificationConfigs.Add(new NotificationConfigEntity
        {
            ProviderId = "discord",
            Key = "channel_id",
            Value = "456",
            UpdatedAt = DateTime.UtcNow
        });

        Assert.ThrowsAny<Exception>(() => _db.SaveChanges());
    }

    [Fact]
    public void NotificationConfig_AllowsSameKeyDifferentProvider()
    {
        _db.NotificationConfigs.Add(new NotificationConfigEntity
        {
            ProviderId = "discord",
            Key = "channel_id",
            Value = "123",
            UpdatedAt = DateTime.UtcNow
        });
        _db.NotificationConfigs.Add(new NotificationConfigEntity
        {
            ProviderId = "slack",
            Key = "channel_id",
            Value = "456",
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        var configs = _db.NotificationConfigs.Where(c => c.Key == "channel_id").ToList();
        Assert.Equal(2, configs.Count);
    }

    [Fact]
    public void NotificationConfig_CanGroupByProvider()
    {
        _db.NotificationConfigs.AddRange(
            new NotificationConfigEntity { ProviderId = "discord", Key = "bot_token", Value = "tok", UpdatedAt = DateTime.UtcNow },
            new NotificationConfigEntity { ProviderId = "discord", Key = "channel_id", Value = "ch1", UpdatedAt = DateTime.UtcNow },
            new NotificationConfigEntity { ProviderId = "discord", Key = "guild_id", Value = "g1", UpdatedAt = DateTime.UtcNow }
        );
        _db.SaveChanges();

        var groups = _db.NotificationConfigs
            .GroupBy(c => c.ProviderId)
            .ToList();

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count());

        var dict = groups[0].ToDictionary(c => c.Key, c => c.Value);
        Assert.Equal("tok", dict["bot_token"]);
        Assert.Equal("ch1", dict["channel_id"]);
        Assert.Equal("g1", dict["guild_id"]);
    }
}
