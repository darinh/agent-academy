using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentAcademy.Server.Tests;

public sealed class ConversationExportServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentAcademyDbContext _db;
    private static int _idCounter;

    public ConversationExportServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        _db = _serviceProvider.GetRequiredService<AgentAcademyDbContext>();
        _db.Database.EnsureCreated();

        // Seed a default room for DM messages (FK constraint requires a room)
        _db.Rooms.Add(new RoomEntity
        {
            Id = "dm-room",
            Name = "DM Room",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private ConversationExportService CreateService() => new(_db);

    private static string NextId() => $"msg-{Interlocked.Increment(ref _idCounter):D6}";

    private RoomEntity SeedRoom(string name = "test-room")
    {
        var room = new RoomEntity
        {
            Id = $"room-{Interlocked.Increment(ref _idCounter)}",
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Rooms.Add(room);
        _db.SaveChanges();
        return room;
    }

    private MessageEntity SeedRoomMessage(string roomId, string sender, string content, DateTime? sentAt = null)
    {
        var msg = new MessageEntity
        {
            Id = NextId(),
            RoomId = roomId,
            SenderId = sender,
            SenderName = sender,
            SenderKind = "Agent",
            Kind = "Chat",
            Content = content,
            SentAt = sentAt ?? DateTime.UtcNow,
        };
        _db.Messages.Add(msg);
        _db.SaveChanges();
        return msg;
    }

    private MessageEntity SeedDmMessage(string senderId, string recipientId, string content, DateTime? sentAt = null)
    {
        var msg = new MessageEntity
        {
            Id = NextId(),
            RoomId = "dm-room",
            SenderId = senderId,
            SenderName = senderId,
            SenderKind = senderId == "human" || senderId == "consultant" ? "User" : "Agent",
            Kind = "DirectMessage",
            Content = content,
            SentAt = sentAt ?? DateTime.UtcNow,
            RecipientId = recipientId,
        };
        _db.Messages.Add(msg);
        _db.SaveChanges();
        return msg;
    }

    // ── Room Export ──────────────────────────────────────────────

    [Fact]
    public async Task GetRoomMessagesForExport_ReturnsNull_WhenRoomNotFound()
    {
        var svc = CreateService();
        var result = await svc.GetRoomMessagesForExportAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetRoomMessagesForExport_ReturnsMessages_OrderedByTime()
    {
        var svc = CreateService();
        var room = SeedRoom();
        var t0 = DateTime.UtcNow;
        SeedRoomMessage(room.Id, "agent-1", "First", t0);
        SeedRoomMessage(room.Id, "agent-2", "Second", t0.AddSeconds(1));
        SeedRoomMessage(room.Id, "human", "Third", t0.AddSeconds(2));

        var result = await svc.GetRoomMessagesForExportAsync(room.Id);
        Assert.NotNull(result);
        var (returnedRoom, messages, truncated) = result.Value;

        Assert.Equal(room.Id, returnedRoom.Id);
        Assert.False(truncated);
        Assert.Equal(3, messages.Count);
        Assert.Equal("First", messages[0].Content);
        Assert.Equal("Second", messages[1].Content);
        Assert.Equal("Third", messages[2].Content);
    }

    [Fact]
    public async Task GetRoomMessagesForExport_ExcludesDmMessages()
    {
        var svc = CreateService();
        var room = SeedRoom();
        SeedRoomMessage(room.Id, "agent-1", "Room message");

        // Add a DM in the same room ID (shouldn't happen in practice, but test the filter)
        var dm = new MessageEntity
        {
            Id = NextId(),
            RoomId = room.Id,
            SenderId = "human",
            SenderName = "human",
            SenderKind = "User",
            Kind = "DirectMessage",
            Content = "DM message",
            SentAt = DateTime.UtcNow,
            RecipientId = "agent-1",
        };
        _db.Messages.Add(dm);
        await _db.SaveChangesAsync();

        var result = await svc.GetRoomMessagesForExportAsync(room.Id);
        Assert.NotNull(result);
        Assert.Single(result.Value.Messages);
        Assert.Equal("Room message", result.Value.Messages[0].Content);
    }

    [Fact]
    public async Task GetRoomMessagesForExport_ReturnsEmptyList_WhenNoMessages()
    {
        var svc = CreateService();
        var room = SeedRoom();

        var result = await svc.GetRoomMessagesForExportAsync(room.Id);
        Assert.NotNull(result);
        Assert.Empty(result.Value.Messages);
        Assert.False(result.Value.Truncated);
    }

    // ── DM Export ───────────────────────────────────────────────

    [Fact]
    public async Task GetDmMessagesForExport_ReturnsNull_WhenNoThread()
    {
        var svc = CreateService();
        var result = await svc.GetDmMessagesForExportAsync("nonexistent-agent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetDmMessagesForExport_ReturnsBothDirections()
    {
        var svc = CreateService();
        var t0 = DateTime.UtcNow;
        SeedDmMessage("human", "agent-1", "Hello from human", t0);
        SeedDmMessage("agent-1", "human", "Hello from agent", t0.AddSeconds(1));
        SeedDmMessage("consultant", "agent-1", "Hello from consultant", t0.AddSeconds(2));

        var result = await svc.GetDmMessagesForExportAsync("agent-1");
        Assert.NotNull(result);
        var (agentId, messages, truncated) = result.Value;

        Assert.Equal("agent-1", agentId);
        Assert.False(truncated);
        Assert.Equal(3, messages.Count);
        Assert.Equal("Hello from human", messages[0].Content);
        Assert.Equal("Hello from agent", messages[1].Content);
        Assert.Equal("Hello from consultant", messages[2].Content);
    }

    [Fact]
    public async Task GetDmMessagesForExport_ExcludesOtherAgentThreads()
    {
        var svc = CreateService();
        SeedDmMessage("human", "agent-1", "Message for agent 1");
        SeedDmMessage("human", "agent-2", "Message for agent 2");

        var result = await svc.GetDmMessagesForExportAsync("agent-1");
        Assert.NotNull(result);
        Assert.Single(result.Value.Messages);
        Assert.Equal("Message for agent 1", result.Value.Messages[0].Content);
    }

    // ── JSON Formatting ─────────────────────────────────────────

    [Fact]
    public void FormatAsJson_ProducesValidJson_WithRoomMetadata()
    {
        var messages = new List<MessageEntity>
        {
            new()
            {
                Id = "m1", RoomId = "r1", SenderId = "agent-1", SenderName = "Planner",
                SenderRole = "Planner", SenderKind = "Agent", Kind = "Chat",
                Content = "Hello world", SentAt = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            },
        };

        var json = ConversationExportService.FormatAsJson(messages, roomName: "Main Room");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Main Room", root.GetProperty("roomName").GetString());
        Assert.Equal(1, root.GetProperty("messageCount").GetInt32());

        var msgs = root.GetProperty("messages");
        Assert.Equal(1, msgs.GetArrayLength());
        Assert.Equal("Hello world", msgs[0].GetProperty("content").GetString());
        Assert.Equal("Planner", msgs[0].GetProperty("senderName").GetString());
    }

    [Fact]
    public void FormatAsJson_ProducesValidJson_WithDmMetadata()
    {
        var messages = new List<MessageEntity>
        {
            new()
            {
                Id = "m1", RoomId = "dm", SenderId = "human", SenderName = "human",
                SenderKind = "User", Kind = "DirectMessage",
                Content = "Hi agent", SentAt = DateTime.UtcNow,
                RecipientId = "agent-1",
            },
        };

        var json = ConversationExportService.FormatAsJson(messages, agentId: "agent-1");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("agent-1", root.GetProperty("agentId").GetString());
        Assert.True(root.GetProperty("roomName").ValueKind == JsonValueKind.Null);
    }

    // ── Markdown Formatting ─────────────────────────────────────

    [Fact]
    public void FormatAsMarkdown_ProducesReadableOutput_WithRoomHeader()
    {
        var messages = new List<MessageEntity>
        {
            new()
            {
                Id = "m1", RoomId = "r1", SenderId = "agent-1", SenderName = "Planner",
                SenderRole = "Planner", SenderKind = "Agent", Kind = "Chat",
                Content = "Task completed.", SentAt = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                Id = "m2", RoomId = "r1", SenderId = "human", SenderName = "Human",
                SenderKind = "User", Kind = "Chat",
                Content = "Good work!", SentAt = new DateTime(2025, 1, 15, 10, 1, 0, DateTimeKind.Utc),
            },
        };

        var md = ConversationExportService.FormatAsMarkdown(messages, roomName: "Main Room");

        Assert.Contains("# Room: Main Room", md);
        Assert.Contains("Messages: 2", md);
        Assert.Contains("**Planner** (Planner)", md);
        Assert.Contains("Task completed.", md);
        Assert.Contains("**Human**", md);
        Assert.Contains("Good work!", md);
    }

    [Fact]
    public void FormatAsMarkdown_ShowsDmHeader_WhenAgentIdProvided()
    {
        var messages = new List<MessageEntity>
        {
            new()
            {
                Id = "m1", RoomId = "dm", SenderId = "human", SenderName = "human",
                SenderKind = "User", Kind = "DirectMessage",
                Content = "Hello", SentAt = DateTime.UtcNow, RecipientId = "agent-1",
            },
        };

        var md = ConversationExportService.FormatAsMarkdown(messages, agentId: "agent-1");
        Assert.Contains("# DM Thread: agent-1", md);
    }

    [Fact]
    public void FormatAsMarkdown_ShowsDateRange_WhenMultipleMessages()
    {
        var t0 = new DateTime(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2025, 3, 1, 12, 30, 0, DateTimeKind.Utc);
        var messages = new List<MessageEntity>
        {
            new()
            {
                Id = "m1", RoomId = "r1", SenderId = "a", SenderName = "A",
                SenderKind = "Agent", Kind = "Chat", Content = "Start", SentAt = t0,
            },
            new()
            {
                Id = "m2", RoomId = "r1", SenderId = "b", SenderName = "B",
                SenderKind = "Agent", Kind = "Chat", Content = "End", SentAt = t1,
            },
        };

        var md = ConversationExportService.FormatAsMarkdown(messages, roomName: "Test");
        Assert.Contains("2025-03-01 08:00:00", md);
        Assert.Contains("2025-03-01 12:30:00", md);
    }

    [Fact]
    public void FormatAsJson_EmptyMessages_ProducesEmptyArray()
    {
        var json = ConversationExportService.FormatAsJson(new List<MessageEntity>(), roomName: "Empty Room");
        var doc = JsonDocument.Parse(json);

        Assert.Equal(0, doc.RootElement.GetProperty("messageCount").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void FormatAsMarkdown_EmptyMessages_ProducesHeaderOnly()
    {
        var md = ConversationExportService.FormatAsMarkdown(new List<MessageEntity>(), roomName: "Empty");
        Assert.Contains("# Room: Empty", md);
        Assert.Contains("Messages: 0", md);
        Assert.DoesNotContain("Date range:", md);
    }
}
