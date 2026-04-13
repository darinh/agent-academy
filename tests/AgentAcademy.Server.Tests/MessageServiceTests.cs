using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

[Collection("WorkspaceRuntime")]
public class MessageServiceTests : IDisposable
{
    private readonly List<IServiceScope> _scopes = [];
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    public MessageServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true)
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddScoped<MessageService>();
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<SystemSettingsService>();
        services.AddSingleton<IAgentExecutor>(NSubstitute.Substitute.For<IAgentExecutor>());
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var scope in _scopes) scope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────

    private async Task<AgentAcademyDbContext> GetDbAsync()
    {
        var scope = _serviceProvider.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        return await Task.FromResult(db);
    }

    private MessageService CreateService()
    {
        var scope = _serviceProvider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<MessageService>();
    }

    private async Task SeedRoomAsync(string roomId, string name = "Test Room", string status = "Active")
    {
        var db = await GetDbAsync();
        db.Rooms.Add(new RoomEntity
        {
            Id = roomId,
            Name = name,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedBreakoutRoomAsync(
        string breakoutRoomId, string parentRoomId, string status = "Active",
        string agentId = "engineer-1")
    {
        var db = await GetDbAsync();
        db.BreakoutRooms.Add(new BreakoutRoomEntity
        {
            Id = breakoutRoomId,
            Name = $"Breakout-{breakoutRoomId}",
            ParentRoomId = parentRoomId,
            AssignedAgentId = agentId,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentLocationAsync(string agentId, string roomId)
    {
        var db = await GetDbAsync();
        db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = agentId,
            RoomId = roomId,
            State = "Idle",
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedDirectMessageAsync(
        string senderId, string senderName, string recipientId,
        string content, string roomId = "main",
        DateTime? sentAt = null, DateTime? acknowledgedAt = null)
    {
        var db = await GetDbAsync();
        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RoomId = roomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderKind = senderId == "human" || senderId == "consultant"
                ? nameof(MessageSenderKind.User)
                : nameof(MessageSenderKind.Agent),
            Kind = nameof(MessageKind.DirectMessage),
            Content = content,
            SentAt = sentAt ?? DateTime.UtcNow,
            RecipientId = recipientId,
            AcknowledgedAt = acknowledgedAt
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedRoomMessagesAsync(string roomId, int count)
    {
        var db = await GetDbAsync();
        for (var i = 0; i < count; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RoomId = roomId,
                SenderId = "system",
                SenderName = "System",
                SenderKind = nameof(MessageSenderKind.System),
                Kind = nameof(MessageKind.System),
                Content = $"Message {i}",
                SentAt = DateTime.UtcNow.AddSeconds(-count + i)
            });
        }
        await db.SaveChangesAsync();
    }

    // ── PostMessageAsync ─────────────────────────────────────────

    [Fact]
    public async Task PostMessageAsync_Success_ReturnsEnvelope()
    {
        await SeedRoomAsync("room-1");
        var svc = CreateService();

        var result = await svc.PostMessageAsync(new PostMessageRequest(
            RoomId: "room-1",
            SenderId: "planner-1",
            Content: "Hello from Aristotle"));

        Assert.NotNull(result);
        Assert.Equal("room-1", result.RoomId);
        Assert.Equal("planner-1", result.SenderId);
        Assert.Equal("Aristotle", result.SenderName);
        Assert.Equal("Planner", result.SenderRole);
        Assert.Equal(MessageSenderKind.Agent, result.SenderKind);
        Assert.Equal(MessageKind.Response, result.Kind);
        Assert.Equal("Hello from Aristotle", result.Content);
        Assert.NotEmpty(result.Id);
    }

    [Fact]
    public async Task PostMessageAsync_PersistsToDatabase()
    {
        await SeedRoomAsync("room-1");
        var svc = CreateService();

        var result = await svc.PostMessageAsync(new PostMessageRequest(
            RoomId: "room-1", SenderId: "engineer-1", Content: "Building it"));

        var db = await GetDbAsync();
        var entity = await db.Messages.FindAsync(result.Id);
        Assert.NotNull(entity);
        Assert.Equal("engineer-1", entity.SenderId);
        Assert.Equal("Building it", entity.Content);
    }

    [Fact]
    public async Task PostMessageAsync_MissingRoom_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PostMessageAsync(new PostMessageRequest(
                RoomId: "nonexistent", SenderId: "planner-1", Content: "Hi")));
    }

    [Fact]
    public async Task PostMessageAsync_MissingAgent_Throws()
    {
        await SeedRoomAsync("room-1");
        var svc = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PostMessageAsync(new PostMessageRequest(
                RoomId: "room-1", SenderId: "unknown-agent", Content: "Hi")));
    }

    [Fact]
    public async Task PostMessageAsync_EmptyRoomId_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.PostMessageAsync(new PostMessageRequest(
                RoomId: "", SenderId: "planner-1", Content: "Hi")));
    }

    [Fact]
    public async Task PostMessageAsync_EmptySenderId_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.PostMessageAsync(new PostMessageRequest(
                RoomId: "room-1", SenderId: "", Content: "Hi")));
    }

    [Fact]
    public async Task PostMessageAsync_EmptyContent_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.PostMessageAsync(new PostMessageRequest(
                RoomId: "room-1", SenderId: "planner-1", Content: "")));
    }

    // ── PostHumanMessageAsync ────────────────────────────────────

    [Fact]
    public async Task PostHumanMessageAsync_Success_WithDefaults()
    {
        await SeedRoomAsync("room-1");
        var svc = CreateService();

        var result = await svc.PostHumanMessageAsync("room-1", "Hello from human");

        Assert.Equal("room-1", result.RoomId);
        Assert.Equal("human", result.SenderId);
        Assert.Equal("Human", result.SenderName);
        Assert.Equal("Human", result.SenderRole);
        Assert.Equal(MessageSenderKind.User, result.SenderKind);
        Assert.Equal("Hello from human", result.Content);
    }

    [Fact]
    public async Task PostHumanMessageAsync_WithConsultantIdentity()
    {
        await SeedRoomAsync("room-1");
        var svc = CreateService();

        var result = await svc.PostHumanMessageAsync(
            "room-1", "Consultant message",
            userId: "consultant", userName: "Consultant", userRole: "Consultant");

        Assert.Equal("consultant", result.SenderId);
        Assert.Equal("Consultant", result.SenderName);
        Assert.Equal("Consultant", result.SenderRole);
    }

    [Fact]
    public async Task PostHumanMessageAsync_EmptyRoomId_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.PostHumanMessageAsync("", "Hello"));
    }

    [Fact]
    public async Task PostHumanMessageAsync_EmptyContent_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.PostHumanMessageAsync("room-1", ""));
    }

    [Fact]
    public async Task PostHumanMessageAsync_MissingRoom_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PostHumanMessageAsync("nonexistent", "Hello"));
    }

    // ── PostSystemMessageAsync ───────────────────────────────────

    [Fact]
    public async Task PostSystemMessageAsync_Success()
    {
        await SeedRoomAsync("room-1");
        var svc = CreateService();

        await svc.PostSystemMessageAsync("room-1", "Agent joined the room");

        var db = await GetDbAsync();
        var msg = await db.Messages.FirstOrDefaultAsync(
            m => m.RoomId == "room-1" && m.SenderId == "system");
        Assert.NotNull(msg);
        Assert.Equal("Agent joined the room", msg.Content);
        Assert.Equal(nameof(MessageSenderKind.System), msg.SenderKind);
        Assert.Equal(nameof(MessageKind.System), msg.Kind);
    }

    [Fact]
    public async Task PostSystemMessageAsync_MissingRoom_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PostSystemMessageAsync("nonexistent", "hello"));
    }

    // ── PostSystemStatusAsync ────────────────────────────────────

    [Fact]
    public async Task PostSystemStatusAsync_Success()
    {
        await SeedRoomAsync("room-1");
        var svc = CreateService();

        await svc.PostSystemStatusAsync("room-1", "Build passed");

        var db = await GetDbAsync();
        var msg = await db.Messages.FirstOrDefaultAsync(
            m => m.RoomId == "room-1" && m.Kind == nameof(MessageKind.System));
        Assert.NotNull(msg);
        Assert.Equal("Build passed", msg.Content);
    }

    [Fact]
    public async Task PostSystemStatusAsync_MissingRoom_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PostSystemStatusAsync("nonexistent", "status"));
    }

    // ── SendDirectMessageAsync ───────────────────────────────────

    [Fact]
    public async Task SendDirectMessageAsync_AgentToAgent_CreatesSystemNotification()
    {
        await SeedRoomAsync("room-1");
        await SeedAgentLocationAsync("engineer-1", "room-1");
        var svc = CreateService();

        var messageId = await svc.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner",
            "engineer-1", "Please review the plan", "room-1");

        Assert.NotEmpty(messageId);

        var db = await GetDbAsync();
        var dm = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId);
        Assert.NotNull(dm);
        Assert.Equal("engineer-1", dm.RecipientId);
        Assert.Equal("Please review the plan", dm.Content);

        var sysNotif = await db.Messages.FirstOrDefaultAsync(
            m => m.SenderId == "system" && m.Content.Contains("sent a direct message"));
        Assert.NotNull(sysNotif);
    }

    [Fact]
    public async Task SendDirectMessageAsync_AgentToHuman_NoSystemNotification()
    {
        await SeedRoomAsync("room-1");
        var svc = CreateService();

        var messageId = await svc.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner",
            "human", "Hey human, need your input", "room-1");

        Assert.NotEmpty(messageId);

        var db = await GetDbAsync();
        var sysNotif = await db.Messages.FirstOrDefaultAsync(
            m => m.SenderId == "system" && m.Content.Contains("sent a direct message"));
        Assert.Null(sysNotif);
    }

    [Fact]
    public async Task SendDirectMessageAsync_ReturnsValidMessageId()
    {
        await SeedRoomAsync("room-1");
        var svc = CreateService();

        var messageId = await svc.SendDirectMessageAsync(
            "engineer-1", "Hephaestus", "SoftwareEngineer",
            "planner-1", "Task done", "room-1");

        Assert.NotEmpty(messageId);
        Assert.Equal(32, messageId.Length); // Guid.ToString("N") is 32 chars
    }

    // ── GetDirectMessagesForAgentAsync ───────────────────────────

    [Fact]
    public async Task GetDirectMessagesForAgentAsync_ReturnsUnreadByDefault()
    {
        await SeedRoomAsync("room-1");
        await SeedDirectMessageAsync("planner-1", "Aristotle", "engineer-1", "Unread message", "room-1");
        await SeedDirectMessageAsync("planner-1", "Aristotle", "engineer-1", "Read message", "room-1",
            acknowledgedAt: DateTime.UtcNow);

        var svc = CreateService();
        var result = await svc.GetDirectMessagesForAgentAsync("engineer-1");

        Assert.Single(result);
        Assert.Equal("Unread message", result[0].Content);
    }

    [Fact]
    public async Task GetDirectMessagesForAgentAsync_ReturnsAll_WhenUnreadOnlyFalse()
    {
        await SeedRoomAsync("room-1");
        await SeedDirectMessageAsync("planner-1", "Aristotle", "engineer-1", "Msg 1", "room-1");
        await SeedDirectMessageAsync("engineer-1", "Hephaestus", "planner-1", "Msg 2", "room-1");
        await SeedDirectMessageAsync("planner-1", "Aristotle", "engineer-1", "Msg 3", "room-1",
            acknowledgedAt: DateTime.UtcNow);

        var svc = CreateService();
        var result = await svc.GetDirectMessagesForAgentAsync("engineer-1", unreadOnly: false);

        // Should include: received unread, sent, received acknowledged
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetDirectMessagesForAgentAsync_RespectsLimit()
    {
        await SeedRoomAsync("room-1");
        for (var i = 0; i < 10; i++)
        {
            await SeedDirectMessageAsync("planner-1", "Aristotle", "engineer-1",
                $"DM {i}", "room-1", sentAt: DateTime.UtcNow.AddMinutes(i));
        }

        var svc = CreateService();
        var result = await svc.GetDirectMessagesForAgentAsync("engineer-1", limit: 3);

        Assert.Equal(3, result.Count);
    }

    // ── AcknowledgeDirectMessagesAsync ───────────────────────────

    [Fact]
    public async Task AcknowledgeDirectMessagesAsync_MarksAcknowledged()
    {
        await SeedRoomAsync("room-1");
        await SeedDirectMessageAsync("planner-1", "Aristotle", "engineer-1", "Ack me", "room-1");
        var db = await GetDbAsync();
        var msg = await db.Messages.FirstAsync(m => m.RecipientId == "engineer-1");

        var svc = CreateService();
        await svc.AcknowledgeDirectMessagesAsync("engineer-1", [msg.Id]);

        var db2 = await GetDbAsync();
        var updated = await db2.Messages.FindAsync(msg.Id);
        Assert.NotNull(updated!.AcknowledgedAt);
    }

    [Fact]
    public async Task AcknowledgeDirectMessagesAsync_EmptyList_IsNoOp()
    {
        await SeedRoomAsync("room-1");
        await SeedDirectMessageAsync("planner-1", "Aristotle", "engineer-1", "Unrelated DM", "room-1");

        var svc = CreateService();
        await svc.AcknowledgeDirectMessagesAsync("engineer-1", []);

        // Verify existing message was NOT acknowledged
        var db = await GetDbAsync();
        var msg = await db.Messages.FirstAsync(m => m.RecipientId == "engineer-1");
        Assert.Null(msg.AcknowledgedAt);
    }

    [Fact]
    public async Task AcknowledgeDirectMessagesAsync_IgnoresMessagesForOtherAgents()
    {
        await SeedRoomAsync("room-1");
        await SeedDirectMessageAsync("planner-1", "Aristotle", "engineer-1", "For engineer", "room-1");
        var db = await GetDbAsync();
        var msg = await db.Messages.FirstAsync(m => m.RecipientId == "engineer-1");

        // Try to acknowledge as a different agent
        var svc = CreateService();
        await svc.AcknowledgeDirectMessagesAsync("planner-1", [msg.Id]);

        var db2 = await GetDbAsync();
        var unchanged = await db2.Messages.FindAsync(msg.Id);
        Assert.Null(unchanged!.AcknowledgedAt);
    }

    // ── GetDmThreadsForHumanAsync ────────────────────────────────

    [Fact]
    public async Task GetDmThreadsForHumanAsync_GroupsByAgent()
    {
        await SeedRoomAsync("room-1");
        await SeedDirectMessageAsync("human", "Human", "planner-1", "Hi planner", "room-1",
            sentAt: DateTime.UtcNow.AddMinutes(-2));
        await SeedDirectMessageAsync("planner-1", "Aristotle", "human", "Hello", "room-1",
            sentAt: DateTime.UtcNow.AddMinutes(-1));
        await SeedDirectMessageAsync("human", "Human", "engineer-1", "Hi engineer", "room-1");

        var svc = CreateService();
        var threads = await svc.GetDmThreadsForHumanAsync();

        Assert.Equal(2, threads.Count);
        var plannerThread = threads.FirstOrDefault(t => t.AgentId == "planner-1");
        var engineerThread = threads.FirstOrDefault(t => t.AgentId == "engineer-1");
        Assert.NotNull(plannerThread);
        Assert.NotNull(engineerThread);
        Assert.Equal("Aristotle", plannerThread.AgentName);
        Assert.Equal(2, plannerThread.MessageCount);
        Assert.Equal(1, engineerThread.MessageCount);
    }

    [Fact]
    public async Task GetDmThreadsForHumanAsync_IncludesConsultantMessages()
    {
        await SeedRoomAsync("room-1");
        await SeedDirectMessageAsync("consultant", "Consultant", "engineer-1", "From consultant", "room-1",
            sentAt: DateTime.UtcNow.AddMinutes(-1));
        await SeedDirectMessageAsync("human", "Human", "engineer-1", "From human", "room-1");

        var svc = CreateService();
        var threads = await svc.GetDmThreadsForHumanAsync();

        Assert.Single(threads);
        Assert.Equal("engineer-1", threads[0].AgentId);
        Assert.Equal(2, threads[0].MessageCount);
    }

    [Fact]
    public async Task GetDmThreadsForHumanAsync_TruncatesLongMessages()
    {
        await SeedRoomAsync("room-1");
        var longMessage = new string('x', 200);
        await SeedDirectMessageAsync("human", "Human", "planner-1", longMessage, "room-1");

        var svc = CreateService();
        var threads = await svc.GetDmThreadsForHumanAsync();

        Assert.Single(threads);
        Assert.True(threads[0].LastMessage.Length <= 104); // 100 chars + "…"
    }

    // ── GetDmThreadMessagesAsync ─────────────────────────────────

    [Fact]
    public async Task GetDmThreadMessagesAsync_ReturnsThreadMessages()
    {
        await SeedRoomAsync("room-1");
        await SeedDirectMessageAsync("human", "Human", "planner-1", "Msg 1", "room-1",
            sentAt: DateTime.UtcNow.AddMinutes(-2));
        await SeedDirectMessageAsync("planner-1", "Aristotle", "human", "Msg 2", "room-1",
            sentAt: DateTime.UtcNow.AddMinutes(-1));
        // Unrelated DM — different agent thread
        await SeedDirectMessageAsync("human", "Human", "engineer-1", "Other", "room-1");

        var svc = CreateService();
        var messages = await svc.GetDmThreadMessagesAsync("planner-1");

        Assert.Equal(2, messages.Count);
        Assert.Equal("Msg 1", messages[0].Content);
        Assert.Equal("Msg 2", messages[1].Content);
    }

    [Fact]
    public async Task GetDmThreadMessagesAsync_RespectsLimit()
    {
        await SeedRoomAsync("room-1");
        for (var i = 0; i < 10; i++)
        {
            await SeedDirectMessageAsync("human", "Human", "planner-1",
                $"Thread msg {i}", "room-1", sentAt: DateTime.UtcNow.AddMinutes(i));
        }

        var svc = CreateService();
        var messages = await svc.GetDmThreadMessagesAsync("planner-1", limit: 5);

        Assert.Equal(5, messages.Count);
    }

    // ── PostBreakoutMessageAsync ─────────────────────────────────

    [Fact]
    public async Task PostBreakoutMessageAsync_Success()
    {
        await SeedRoomAsync("room-1");
        await SeedBreakoutRoomAsync("br-1", "room-1");
        var svc = CreateService();

        await svc.PostBreakoutMessageAsync(
            "br-1", "engineer-1", "Hephaestus", "SoftwareEngineer", "Working on it");

        var db = await GetDbAsync();
        var msg = await db.BreakoutMessages.FirstOrDefaultAsync(
            m => m.BreakoutRoomId == "br-1");
        Assert.NotNull(msg);
        Assert.Equal("Working on it", msg.Content);
        Assert.Equal("engineer-1", msg.SenderId);
    }

    [Fact]
    public async Task PostBreakoutMessageAsync_ArchivedBreakout_Throws()
    {
        await SeedRoomAsync("room-1");
        await SeedBreakoutRoomAsync("br-1", "room-1", status: "Archived");
        var svc = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PostBreakoutMessageAsync(
                "br-1", "engineer-1", "Hephaestus", "SoftwareEngineer", "content"));
    }

    [Fact]
    public async Task PostBreakoutMessageAsync_MissingBreakout_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PostBreakoutMessageAsync(
                "nonexistent", "engineer-1", "Hephaestus", "SoftwareEngineer", "content"));
    }

    // ── TrimMessagesAsync ────────────────────────────────────────

    [Fact]
    public async Task TrimMessagesAsync_TrimsWhenOverLimit()
    {
        await SeedRoomAsync("room-1");
        await SeedRoomMessagesAsync("room-1", 210);

        // TrimMessagesAsync marks entities for deletion but doesn't call
        // SaveChangesAsync — the caller is responsible for that. Use the
        // same scope so we can persist the removals.
        using var scope = _serviceProvider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MessageService>();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        await svc.TrimMessagesAsync("room-1");
        await db.SaveChangesAsync();

        var remaining = await db.Messages.CountAsync(m => m.RoomId == "room-1");
        Assert.True(remaining <= 200, $"Expected <= 200 but got {remaining}");
    }

    [Fact]
    public async Task TrimMessagesAsync_NoOp_WhenUnderLimit()
    {
        await SeedRoomAsync("room-1");
        await SeedRoomMessagesAsync("room-1", 50);
        var svc = CreateService();

        await svc.TrimMessagesAsync("room-1");

        var db = await GetDbAsync();
        var count = await db.Messages.CountAsync(m => m.RoomId == "room-1");
        Assert.Equal(50, count);
    }

    [Fact]
    public async Task TrimMessagesAsync_DoesNotTrimDirectMessages()
    {
        await SeedRoomAsync("room-1");
        await SeedRoomMessagesAsync("room-1", 200);
        for (var i = 0; i < 10; i++)
        {
            await SeedDirectMessageAsync("planner-1", "Aristotle", "engineer-1",
                $"DM {i}", "room-1");
        }

        using var scope = _serviceProvider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MessageService>();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        await svc.TrimMessagesAsync("room-1");
        await db.SaveChangesAsync();

        var dmCount = await db.Messages.CountAsync(
            m => m.RoomId == "room-1" && m.RecipientId != null);
        Assert.Equal(10, dmCount);
    }

    // ── CreateMessageEntity ──────────────────────────────────────

    [Fact]
    public void CreateMessageEntity_SetsCorrectFields()
    {
        var svc = CreateService();
        var sentAt = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        var entity = svc.CreateMessageEntity(
            "room-1", MessageKind.System, "System restarted", "corr-123", sentAt);

        Assert.Equal("room-1", entity.RoomId);
        Assert.Equal("system", entity.SenderId);
        Assert.Equal("System", entity.SenderName);
        Assert.Equal(nameof(MessageSenderKind.System), entity.SenderKind);
        Assert.Equal(nameof(MessageKind.System), entity.Kind);
        Assert.Equal("System restarted", entity.Content);
        Assert.Equal(sentAt, entity.SentAt);
        Assert.Equal("corr-123", entity.CorrelationId);
        Assert.NotEmpty(entity.Id);
    }

    [Fact]
    public void CreateMessageEntity_NullCorrelationId()
    {
        var svc = CreateService();

        var entity = svc.CreateMessageEntity(
            "room-1", MessageKind.Coordination, "Coordinating", null, DateTime.UtcNow);

        Assert.Null(entity.CorrelationId);
        Assert.Equal(nameof(MessageKind.Coordination), entity.Kind);
    }
}
