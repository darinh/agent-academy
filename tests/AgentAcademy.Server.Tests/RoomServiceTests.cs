using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

[Collection("WorkspaceRuntime")]
public class RoomServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    public RoomServiceTests()
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
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton<IMessageBroadcaster>(sp => sp.GetRequiredService<MessageBroadcaster>());
        services.AddScoped<ActivityPublisher>();
        services.AddScoped<IActivityPublisher>(sp => sp.GetRequiredService<ActivityPublisher>());
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<MessageService>();
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddSingleton<AgentAcademy.Server.Services.AgentWatchdog.IWatchdogAgentRunner>(sp =>
            new TestDoubles.NoOpWatchdogAgentRunner(sp.GetRequiredService<IAgentExecutor>()));
        services.AddScoped<ConversationSessionService>();
        services.AddScoped<IConversationSessionService>(sp => sp.GetRequiredService<ConversationSessionService>());
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ISystemSettingsService>(sp => sp.GetRequiredService<SystemSettingsService>());
        services.AddSingleton<IAgentExecutor>(NSubstitute.Substitute.For<IAgentExecutor>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<IPhaseTransitionValidator>(sp => sp.GetRequiredService<PhaseTransitionValidator>());
        services.AddScoped<RoomService>();
        services.AddScoped<IRoomService>(sp => sp.GetRequiredService<RoomService>());
        services.AddScoped<RoomSnapshotBuilder>();

        services.AddScoped<IRoomSnapshotBuilder>(sp => sp.GetRequiredService<RoomSnapshotBuilder>());
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ────────────────────────────────────────────────────

    private IServiceScope CreateScope() => _serviceProvider.CreateScope();

    private static RoomEntity MakeRoom(
        string id, string name, string? workspacePath = null,
        string status = "Idle", string phase = "Intake", string? topic = null)
    {
        var now = DateTime.UtcNow;
        return new RoomEntity
        {
            Id = id,
            Name = name,
            Status = status,
            CurrentPhase = phase,
            WorkspacePath = workspacePath,
            Topic = topic,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static WorkspaceEntity MakeWorkspace(
        string path, string? projectName = null, bool isActive = false)
    {
        return new WorkspaceEntity
        {
            Path = path,
            ProjectName = projectName,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static MessageEntity MakeMessage(
        string roomId, string senderId, string senderName,
        string senderKind, string content, DateTime? sentAt = null,
        string? sessionId = null, string? recipientId = null)
    {
        return new MessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RoomId = roomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderKind = senderKind,
            Kind = nameof(MessageKind.Response),
            Content = content,
            SentAt = sentAt ?? DateTime.UtcNow,
            SessionId = sessionId,
            RecipientId = recipientId
        };
    }

    private static TaskEntity MakeTask(
        string id, string roomId, string status = "Active",
        string phase = "Implementation")
    {
        var now = DateTime.UtcNow;
        return new TaskEntity
        {
            Id = id,
            Title = $"Task {id}",
            Description = "Test task",
            Status = status,
            CurrentPhase = phase,
            RoomId = roomId,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    // ── GetRoomsAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetRoomsAsync_ReturnsRoomsForActiveWorkspace()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Workspaces.Add(MakeWorkspace("/projects/alpha", "Alpha", isActive: true));
        db.Rooms.Add(MakeRoom("r1", "Alpha Room", workspacePath: "/projects/alpha"));
        db.Rooms.Add(MakeRoom("r2", "Beta Room", workspacePath: "/projects/beta"));
        await db.SaveChangesAsync();

        var rooms = await svc.GetRoomsAsync();

        Assert.Single(rooms);
        Assert.Equal("r1", rooms[0].Id);
    }

    [Fact]
    public async Task GetRoomsAsync_ExcludesArchivedByDefault()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Active Room"));
        db.Rooms.Add(MakeRoom("r2", "Archived Room", status: nameof(RoomStatus.Archived)));
        await db.SaveChangesAsync();

        var rooms = await svc.GetRoomsAsync();

        Assert.Single(rooms);
        Assert.Equal("r1", rooms[0].Id);
    }

    [Fact]
    public async Task GetRoomsAsync_IncludesArchivedWhenRequested()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Active Room"));
        db.Rooms.Add(MakeRoom("r2", "Archived Room", status: nameof(RoomStatus.Archived)));
        await db.SaveChangesAsync();

        var rooms = await svc.GetRoomsAsync(includeArchived: true);

        Assert.Equal(2, rooms.Count);
    }

    [Fact]
    public async Task GetRoomsAsync_IncludesNullWorkspaceRoomsWhenNoActiveWorkspace()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Global Room"));
        db.Rooms.Add(MakeRoom("r2", "Workspace Room", workspacePath: "/projects/x"));
        await db.SaveChangesAsync();

        var rooms = await svc.GetRoomsAsync();

        Assert.Single(rooms);
        Assert.Equal("r1", rooms[0].Id);
    }

    [Fact]
    public async Task GetRoomsAsync_IncludesNullWorkspaceRoomsWithActiveWorkspace()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Workspaces.Add(MakeWorkspace("/projects/alpha", "Alpha", isActive: true));
        db.Rooms.Add(MakeRoom("r1", "Global Room"));
        db.Rooms.Add(MakeRoom("r2", "Alpha Room", workspacePath: "/projects/alpha"));
        await db.SaveChangesAsync();

        var rooms = await svc.GetRoomsAsync();

        Assert.Equal(2, rooms.Count);
    }

    // ── GetRoomAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetRoomAsync_ReturnsRoomWhenExists()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Test Room"));
        await db.SaveChangesAsync();

        var snapshot = await svc.GetRoomAsync("r1");

        Assert.NotNull(snapshot);
        Assert.Equal("r1", snapshot.Id);
        Assert.Equal("Test Room", snapshot.Name);
    }

    [Fact]
    public async Task GetRoomAsync_ReturnsNullWhenNotFound()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var snapshot = await svc.GetRoomAsync("nonexistent");

        Assert.Null(snapshot);
    }

    // ── GetRoomMessagesAsync ──────────────────────────────────────

    [Fact]
    public async Task GetRoomMessagesAsync_ReturnsMessagesForRoom()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room"));
        db.Messages.Add(MakeMessage("r1", "human", "Human", nameof(MessageSenderKind.User), "Hello"));
        await db.SaveChangesAsync();

        var (messages, hasMore) = await svc.GetRoomMessagesAsync("r1");

        Assert.Single(messages);
        Assert.Equal("Hello", messages[0].Content);
        Assert.False(hasMore);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_RespectsAfterMessageIdCursor()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room"));
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var msg1 = MakeMessage("r1", "human", "Human", nameof(MessageSenderKind.User), "First", baseTime);
        var msg2 = MakeMessage("r1", "human", "Human", nameof(MessageSenderKind.User), "Second", baseTime.AddMinutes(1));
        var msg3 = MakeMessage("r1", "human", "Human", nameof(MessageSenderKind.User), "Third", baseTime.AddMinutes(2));
        db.Messages.AddRange(msg1, msg2, msg3);
        await db.SaveChangesAsync();

        var (messages, _) = await svc.GetRoomMessagesAsync("r1", afterMessageId: msg1.Id);

        Assert.Equal(2, messages.Count);
        Assert.Equal("Second", messages[0].Content);
        Assert.Equal("Third", messages[1].Content);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_RespectsLimit()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room"));
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 5; i++)
        {
            db.Messages.Add(MakeMessage("r1", "human", "Human",
                nameof(MessageSenderKind.User), $"Msg {i}", baseTime.AddMinutes(i)));
        }
        await db.SaveChangesAsync();

        var (messages, hasMore) = await svc.GetRoomMessagesAsync("r1", limit: 3);

        Assert.Equal(3, messages.Count);
        Assert.True(hasMore);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_ClampsLimitToValidRange()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room"));
        db.Messages.Add(MakeMessage("r1", "human", "Human",
            nameof(MessageSenderKind.User), "Msg"));
        await db.SaveChangesAsync();

        // limit=0 should be clamped to 1
        var (messages, _) = await svc.GetRoomMessagesAsync("r1", limit: 0);

        Assert.Single(messages);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_ThrowsForMissingRoom()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetRoomMessagesAsync("nonexistent"));
    }

    [Fact]
    public async Task GetRoomMessagesAsync_HasMoreFlagWhenMoreMessagesExist()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room"));
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 10; i++)
        {
            db.Messages.Add(MakeMessage("r1", "human", "Human",
                nameof(MessageSenderKind.User), $"Msg {i}", baseTime.AddMinutes(i)));
        }
        await db.SaveChangesAsync();

        var (messages, hasMore) = await svc.GetRoomMessagesAsync("r1", limit: 5);
        Assert.True(hasMore);
        Assert.Equal(5, messages.Count);

        var (all, hasMoreAll) = await svc.GetRoomMessagesAsync("r1", limit: 200);
        Assert.False(hasMoreAll);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_ExplicitSession_ReturnsOnlyThatSessionMessages()
    {
        // When an explicit sessionId is provided (archived session view),
        // only that session's messages are returned — no cross-session
        // User leaking, no legacy untagged messages.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room"));
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Messages.Add(MakeMessage("r1", "agent-1", "Agent", nameof(MessageSenderKind.Agent),
            "Session A msg", baseTime, sessionId: "session-a"));
        db.Messages.Add(MakeMessage("r1", "agent-1", "Agent", nameof(MessageSenderKind.Agent),
            "Session B msg", baseTime.AddMinutes(1), sessionId: "session-b"));
        db.Messages.Add(MakeMessage("r1", "human", "Human", nameof(MessageSenderKind.User),
            "Legacy untagged human msg", baseTime.AddMinutes(2)));
        db.Messages.Add(MakeMessage("r1", "human", "Human", nameof(MessageSenderKind.User),
            "Session B human msg", baseTime.AddMinutes(3), sessionId: "session-b"));
        db.Messages.Add(MakeMessage("r1", "human", "Human", nameof(MessageSenderKind.User),
            "Session A human msg", baseTime.AddMinutes(4), sessionId: "session-a"));
        await db.SaveChangesAsync();

        var (messages, _) = await svc.GetRoomMessagesAsync("r1", sessionId: "session-a");

        var contents = messages.Select(m => m.Content).ToList();
        // Messages from the requested session: included (both agent and user).
        Assert.Contains("Session A msg", contents);
        Assert.Contains("Session A human msg", contents);
        // Messages from other sessions: excluded.
        Assert.DoesNotContain("Session B human msg", contents);
        Assert.DoesNotContain("Session B msg", contents);
        // Legacy untagged: excluded from explicit session view.
        Assert.DoesNotContain("Legacy untagged human msg", contents);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_NoActiveSession_ReturnsOnlyUntaggedMessages()
    {
        // When no Active session exists and no explicit sessionId is given,
        // only legacy untagged messages are returned. Prior session messages
        // (both agent and user) are not leaked into the active view.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room"));
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Messages.Add(MakeMessage("r1", "agent-1", "Agent", nameof(MessageSenderKind.Agent),
            "Archived agent msg", baseTime, sessionId: "session-archived"));
        db.Messages.Add(MakeMessage("r1", "human", "Human", nameof(MessageSenderKind.User),
            "Archived human msg", baseTime.AddMinutes(1), sessionId: "session-archived"));
        db.Messages.Add(MakeMessage("r1", "human", "Human", nameof(MessageSenderKind.User),
            "Legacy untagged msg", baseTime.AddMinutes(2)));
        await db.SaveChangesAsync();

        var (messages, _) = await svc.GetRoomMessagesAsync("r1");

        var contents = messages.Select(m => m.Content).ToList();
        Assert.Contains("Legacy untagged msg", contents);
        // Prior session messages: excluded (matches RoomSnapshotBuilder).
        Assert.DoesNotContain("Archived human msg", contents);
        Assert.DoesNotContain("Archived agent msg", contents);
    }

    // ── GetProjectNameForRoomAsync ────────────────────────────────

    [Fact]
    public async Task GetProjectNameForRoomAsync_ReturnsProjectName()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Workspaces.Add(MakeWorkspace("/projects/my-app", "My Application"));
        db.Rooms.Add(MakeRoom("r1", "Room", workspacePath: "/projects/my-app"));
        await db.SaveChangesAsync();

        var name = await svc.GetProjectNameForRoomAsync("r1");

        Assert.Equal("My Application", name);
    }

    [Fact]
    public async Task GetProjectNameForRoomAsync_ReturnsNullForMissingRoom()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var name = await svc.GetProjectNameForRoomAsync("nonexistent");

        Assert.Null(name);
    }

    [Fact]
    public async Task GetProjectNameForRoomAsync_ReturnsBasenameFallback()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Workspaces.Add(MakeWorkspace("/projects/my-cool-app", projectName: null));
        db.Rooms.Add(MakeRoom("r1", "Room", workspacePath: "/projects/my-cool-app"));
        await db.SaveChangesAsync();

        var name = await svc.GetProjectNameForRoomAsync("r1");

        Assert.NotNull(name);
        Assert.NotEmpty(name);
    }

    [Fact]
    public async Task GetProjectNameForRoomAsync_ReturnsNullWhenNoWorkspacePath()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room"));
        await db.SaveChangesAsync();

        var name = await svc.GetProjectNameForRoomAsync("r1");

        Assert.Null(name);
    }

    // ── GetActiveProjectNameAsync ─────────────────────────────────

    [Fact]
    public async Task GetActiveProjectNameAsync_ReturnsProjectName()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Workspaces.Add(MakeWorkspace("/projects/alpha", "Alpha Project", isActive: true));
        await db.SaveChangesAsync();

        var name = await svc.GetActiveProjectNameAsync();

        Assert.Equal("Alpha Project", name);
    }

    [Fact]
    public async Task GetActiveProjectNameAsync_ReturnsNullWhenNoWorkspace()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var name = await svc.GetActiveProjectNameAsync();

        Assert.Null(name);
    }

    // ── GetRoomsWithPendingHumanMessagesAsync ─────────────────────

    [Fact]
    public async Task GetRoomsWithPendingHumanMessagesAsync_ReturnsRoomsWithPendingHumanMessages()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Rooms.Add(MakeRoom("r1", "Room 1"));
        db.Messages.Add(MakeMessage("r1", "human", "Human",
            nameof(MessageSenderKind.User), "Need help", baseTime));
        await db.SaveChangesAsync();

        var result = await svc.GetRoomsWithPendingHumanMessagesAsync();

        Assert.Contains("r1", result);
    }

    [Fact]
    public async Task GetRoomsWithPendingHumanMessagesAsync_ExcludesRoomsWhereAgentResponded()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Rooms.Add(MakeRoom("r1", "Room 1"));
        db.Messages.Add(MakeMessage("r1", "human", "Human",
            nameof(MessageSenderKind.User), "Need help", baseTime));
        db.Messages.Add(MakeMessage("r1", "planner-1", "Aristotle",
            nameof(MessageSenderKind.Agent), "On it!", baseTime.AddMinutes(1)));
        await db.SaveChangesAsync();

        var result = await svc.GetRoomsWithPendingHumanMessagesAsync();

        Assert.DoesNotContain("r1", result);
    }

    [Fact]
    public async Task GetRoomsWithPendingHumanMessagesAsync_ExcludesArchivedRooms()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Rooms.Add(MakeRoom("r1", "Archived Room", status: nameof(RoomStatus.Archived)));
        db.Messages.Add(MakeMessage("r1", "human", "Human",
            nameof(MessageSenderKind.User), "Hello?", baseTime));
        await db.SaveChangesAsync();

        var result = await svc.GetRoomsWithPendingHumanMessagesAsync();

        Assert.DoesNotContain("r1", result);
    }

    // ── CreateRoomAsync ───────────────────────────────────────────

    [Fact]
    public async Task CreateRoomAsync_CreatesRoomWithCorrectProperties()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var snapshot = await svc.CreateRoomAsync("Design Review");

        Assert.Equal("Design Review", snapshot.Name);
        Assert.Equal(RoomStatus.Idle, snapshot.Status);
        Assert.Equal(CollaborationPhase.Intake, snapshot.CurrentPhase);
    }

    [Fact]
    public async Task CreateRoomAsync_GeneratesSlugBasedId()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var snapshot = await svc.CreateRoomAsync("My Test Room");

        Assert.StartsWith("my-test-room-", snapshot.Id);
        Assert.Equal("my-test-room-".Length + 8, snapshot.Id.Length);
    }

    [Fact]
    public async Task CreateRoomAsync_IncludesDescriptionInWelcomeMessage()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var snapshot = await svc.CreateRoomAsync("Sprint Planning", "Plan sprint 5 tasks");

        var welcomeMsg = await db.Messages
            .Where(m => m.RoomId == snapshot.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(welcomeMsg);
        Assert.Contains("Sprint Planning", welcomeMsg.Content);
        Assert.Contains("Plan sprint 5 tasks", welcomeMsg.Content);
    }

    [Fact]
    public async Task CreateRoomAsync_ThrowsOnEmptyName()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.CreateRoomAsync(""));
    }

    [Fact]
    public async Task CreateRoomAsync_ThrowsOnWhitespaceName()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.CreateRoomAsync("   "));
    }

    [Fact]
    public async Task CreateRoomAsync_AssociatesWithActiveWorkspace()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Workspaces.Add(MakeWorkspace("/projects/alpha", "Alpha", isActive: true));
        await db.SaveChangesAsync();

        var snapshot = await svc.CreateRoomAsync("Alpha Room");

        var room = await db.Rooms.FindAsync(snapshot.Id);
        Assert.NotNull(room);
        Assert.Equal("/projects/alpha", room.WorkspacePath);
    }

    // ── RenameRoomAsync ───────────────────────────────────────────

    [Fact]
    public async Task RenameRoomAsync_RenamesSuccessfully()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Old Name"));
        await db.SaveChangesAsync();

        var snapshot = await svc.RenameRoomAsync("r1", "New Name");

        Assert.NotNull(snapshot);
        Assert.Equal("New Name", snapshot.Name);

        var room = await db.Rooms.FindAsync("r1");
        Assert.Equal("New Name", room!.Name);
    }

    [Fact]
    public async Task RenameRoomAsync_ReturnsNullForMissingRoom()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var result = await svc.RenameRoomAsync("nonexistent", "New Name");

        Assert.Null(result);
    }

    // ── SetRoomTopicAsync ─────────────────────────────────────────

    [Fact]
    public async Task SetRoomTopicAsync_SetsTopic()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room"));
        await db.SaveChangesAsync();

        var snapshot = await svc.SetRoomTopicAsync("r1", "Current sprint goals");

        Assert.Equal("Current sprint goals", snapshot.Topic);
    }

    [Fact]
    public async Task SetRoomTopicAsync_ClearsTopicWhenNull()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room", topic: "Old topic"));
        await db.SaveChangesAsync();

        var snapshot = await svc.SetRoomTopicAsync("r1", null);

        Assert.Null(snapshot.Topic);
    }

    [Fact]
    public async Task SetRoomTopicAsync_ClearsTopicWhenWhitespace()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room", topic: "Old topic"));
        await db.SaveChangesAsync();

        var snapshot = await svc.SetRoomTopicAsync("r1", "   ");

        Assert.Null(snapshot.Topic);
    }

    [Fact]
    public async Task SetRoomTopicAsync_ThrowsOnArchivedRoom()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room", status: nameof(RoomStatus.Archived)));
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetRoomTopicAsync("r1", "New topic"));
    }

    [Fact]
    public async Task SetRoomTopicAsync_ThrowsOnMissingRoom()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetRoomTopicAsync("nonexistent", "Topic"));
    }

    // ── TransitionPhaseAsync ──────────────────────────────────────

    [Fact]
    public async Task TransitionPhaseAsync_TransitionsPhase()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room", phase: nameof(CollaborationPhase.Intake)));
        await db.SaveChangesAsync();

        var snapshot = await svc.TransitionPhaseAsync("r1", CollaborationPhase.Planning);

        Assert.Equal(CollaborationPhase.Planning, snapshot.CurrentPhase);
        Assert.Equal(RoomStatus.Active, snapshot.Status);
    }

    [Fact]
    public async Task TransitionPhaseAsync_UpdatesActiveTaskPhase()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room", phase: nameof(CollaborationPhase.Planning)));
        db.Tasks.Add(MakeTask("t1", "r1", status: nameof(TaskStatus.Active), phase: "Planning"));
        await db.SaveChangesAsync();

        await svc.TransitionPhaseAsync("r1", CollaborationPhase.Implementation, force: true);

        var task = await db.Tasks.FindAsync("t1");
        Assert.Equal("Implementation", task!.CurrentPhase);
    }

    [Fact]
    public async Task TransitionPhaseAsync_PostsCoordinationMessage()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room", phase: nameof(CollaborationPhase.Intake)));
        await db.SaveChangesAsync();

        await svc.TransitionPhaseAsync("r1", CollaborationPhase.Planning, "Starting planning");

        var messages = await db.Messages.Where(m => m.RoomId == "r1").ToListAsync();
        Assert.Contains(messages, m => m.Content.Contains("Phase changed") && m.Content.Contains("Planning"));
        Assert.Contains(messages, m => m.Content.Contains("Starting planning"));
    }

    [Fact]
    public async Task TransitionPhaseAsync_NoOpWhenAlreadyInTargetPhase()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room", phase: nameof(CollaborationPhase.Implementation)));
        await db.SaveChangesAsync();

        var snapshot = await svc.TransitionPhaseAsync("r1", CollaborationPhase.Implementation);

        Assert.Equal(CollaborationPhase.Implementation, snapshot.CurrentPhase);

        var messages = await db.Messages.Where(m => m.RoomId == "r1").ToListAsync();
        Assert.Empty(messages);
    }

    [Fact]
    public async Task TransitionPhaseAsync_ThrowsOnMissingRoom()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.TransitionPhaseAsync("nonexistent", CollaborationPhase.Planning));
    }

    [Fact]
    public async Task TransitionPhaseAsync_SetsCompletedStatusForFinalSynthesis()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room", phase: nameof(CollaborationPhase.Validation)));
        await db.SaveChangesAsync();

        var snapshot = await svc.TransitionPhaseAsync("r1", CollaborationPhase.FinalSynthesis, force: true);

        Assert.Equal(RoomStatus.Completed, snapshot.Status);
        Assert.Equal(CollaborationPhase.FinalSynthesis, snapshot.CurrentPhase);
    }

    [Fact]
    public async Task TransitionPhaseAsync_SetsActiveStatusForNonFinalPhases()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Rooms.Add(MakeRoom("r1", "Room", phase: nameof(CollaborationPhase.Intake)));
        await db.SaveChangesAsync();

        var snapshot = await svc.TransitionPhaseAsync("r1", CollaborationPhase.Discussion, force: true);

        Assert.Equal(RoomStatus.Active, snapshot.Status);
    }

    // ── GetActiveWorkspacePathAsync ───────────────────────────────

    [Fact]
    public async Task GetActiveWorkspacePathAsync_ReturnsPathWhenActive()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        db.Workspaces.Add(MakeWorkspace("/projects/alpha", "Alpha", isActive: true));
        await db.SaveChangesAsync();

        var path = await svc.GetActiveWorkspacePathAsync();

        Assert.Equal("/projects/alpha", path);
    }

    [Fact]
    public async Task GetActiveWorkspacePathAsync_ReturnsNullWhenNone()
    {
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RoomService>();

        var path = await svc.GetActiveWorkspacePathAsync();

        Assert.Null(path);
    }

    // ── Normalize ─────────────────────────────────────────────────

    [Fact]
    public void Normalize_LowercasesAndReplacesNonAlphanumeric()
    {
        var result = RoomService.Normalize("My Test Room");

        Assert.Equal("my-test-room", result);
    }

    [Fact]
    public void Normalize_HandlesSpecialCharacters()
    {
        var result = RoomService.Normalize("Sprint #5 — Review!!!");

        Assert.Equal("sprint-5-review", result);
    }

    [Fact]
    public void Normalize_TrimsLeadingAndTrailingHyphens()
    {
        var result = RoomService.Normalize("---hello---");

        Assert.Equal("hello", result);
    }

    [Fact]
    public void Normalize_CollapsesConsecutiveNonAlphanumeric()
    {
        var result = RoomService.Normalize("a   b...c");

        Assert.Equal("a-b-c", result);
    }
}
