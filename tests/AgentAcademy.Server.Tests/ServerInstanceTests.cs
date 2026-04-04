using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

[Collection("WorkspaceRuntime")]
public class ServerInstanceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly WorkspaceRuntime _runtime;

    public ServerInstanceTests()
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
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1",
                    Name: "Aristotle",
                    Role: "Planner",
                    Summary: "Planning lead",
                    StartupPrompt: "You are the planner.",
                    Model: null,
                    CapabilityTags: ["planning"],
                    EnabledTools: ["chat"],
                    AutoJoinDefaultRoom: true),
                new AgentDefinition(
                    Id: "engineer-1",
                    Name: "Hephaestus",
                    Role: "SoftwareEngineer",
                    Summary: "Backend engineer",
                    StartupPrompt: "You are the engineer.",
                    Model: null,
                    CapabilityTags: ["implementation"],
                    EnabledTools: ["chat", "code"],
                    AutoJoinDefaultRoom: true)
            ]);

        var activityBus = new ActivityBroadcaster();
        var settingsService = new SystemSettingsService(_db);
        var executor = NSubstitute.Substitute.For<IAgentExecutor>();
        var sessionLogger = NullLogger<ConversationSessionService>.Instance;
        var sessionService = new ConversationSessionService(_db, settingsService, executor, sessionLogger);

        _runtime = new WorkspaceRuntime(
            _db,
            NullLogger<WorkspaceRuntime>.Instance,
            catalog,
            activityBus,
            sessionService);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task InitializeAsync_CreatesServerInstance()
    {
        await _runtime.InitializeAsync();

        var instances = await _db.ServerInstances.ToListAsync();
        Assert.Single(instances);

        var instance = instances[0];
        Assert.NotNull(instance.Id);
        Assert.NotNull(instance.Version);
        Assert.True(instance.StartedAt <= DateTime.UtcNow);
        Assert.Null(instance.ShutdownAt);
        Assert.Null(instance.ExitCode);
        Assert.False(instance.CrashDetected);
    }

    [Fact]
    public async Task InitializeAsync_SetsCurrentInstanceId()
    {
        await _runtime.InitializeAsync();

        Assert.NotNull(WorkspaceRuntime.CurrentInstanceId);
    }

    [Fact]
    public async Task InitializeAsync_DetectsCrash_WhenPreviousInstanceNotShutDown()
    {
        // Simulate a previous instance that didn't shut down
        var orphan = new ServerInstanceEntity
        {
            StartedAt = DateTime.UtcNow.AddHours(-1),
            Version = "1.0.0"
            // ShutdownAt is null — simulates a crash
        };
        _db.ServerInstances.Add(orphan);
        await _db.SaveChangesAsync();

        await _runtime.InitializeAsync();

        var instances = await _db.ServerInstances
            .OrderBy(i => i.StartedAt)
            .ToListAsync();

        Assert.Equal(2, instances.Count);

        // Orphan should be marked as crashed
        var orphanUpdated = instances[0];
        Assert.NotNull(orphanUpdated.ShutdownAt);
        Assert.Equal(-1, orphanUpdated.ExitCode);

        // New instance should have CrashDetected = true
        var current = instances[1];
        Assert.True(current.CrashDetected);
        Assert.Null(current.ShutdownAt);
    }

    [Fact]
    public async Task InitializeAsync_NoCrash_WhenPreviousInstanceShutDownCleanly()
    {
        // Simulate a previous instance that shut down cleanly
        var previous = new ServerInstanceEntity
        {
            StartedAt = DateTime.UtcNow.AddHours(-1),
            ShutdownAt = DateTime.UtcNow.AddMinutes(-30),
            ExitCode = 0,
            Version = "1.0.0"
        };
        _db.ServerInstances.Add(previous);
        await _db.SaveChangesAsync();

        await _runtime.InitializeAsync();

        var instances = await _db.ServerInstances
            .OrderBy(i => i.StartedAt)
            .ToListAsync();

        Assert.Equal(2, instances.Count);

        // New instance should NOT have CrashDetected
        var current = instances[1];
        Assert.False(current.CrashDetected);
    }

    [Fact]
    public void DefaultRoomId_ReturnsConfiguredValue()
    {
        Assert.Equal("main", _runtime.DefaultRoomId);
    }

    [Fact]
    public async Task HandleStartupRecoveryAsync_ClosesBreakouts_ResetsAgents_AndPostsNotification()
    {
        var now = DateTime.UtcNow;

        _db.Rooms.Add(new RoomEntity
        {
            Id = "main",
            Name = "Main Collaboration Room",
            Status = nameof(RoomStatus.Idle),
            CurrentPhase = nameof(CollaborationPhase.Intake),
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddHours(-1)
        });

        _db.BreakoutRooms.Add(new BreakoutRoomEntity
        {
            Id = "breakout-1",
            Name = "Crash Recovery Work",
            ParentRoomId = "main",
            AssignedAgentId = "engineer-1",
            Status = nameof(RoomStatus.Active),
            CreatedAt = now.AddMinutes(-30),
            UpdatedAt = now.AddMinutes(-30)
        });

        _db.AgentLocations.AddRange(
            new AgentLocationEntity
            {
                AgentId = "engineer-1",
                RoomId = "main",
                State = nameof(AgentState.Working),
                BreakoutRoomId = "breakout-1",
                UpdatedAt = now.AddMinutes(-30)
            },
            new AgentLocationEntity
            {
                AgentId = "planner-1",
                RoomId = "main",
                State = nameof(AgentState.Working),
                UpdatedAt = now.AddMinutes(-20)
            });

        _db.ServerInstances.Add(new ServerInstanceEntity
        {
            StartedAt = now.AddHours(-2),
            Version = "1.0.0"
        });

        await _db.SaveChangesAsync();
        await _runtime.InitializeAsync();

        var breakoutBeforeRecovery = await _db.BreakoutRooms.FindAsync("breakout-1");
        Assert.NotNull(breakoutBeforeRecovery);
        Assert.Equal(nameof(RoomStatus.Active), breakoutBeforeRecovery.Status);
        Assert.Null(breakoutBeforeRecovery.CloseReason);

        var engineerBeforeRecovery = await _db.AgentLocations.FindAsync("engineer-1");
        Assert.NotNull(engineerBeforeRecovery);
        Assert.Equal(nameof(AgentState.Working), engineerBeforeRecovery.State);
        Assert.Equal("breakout-1", engineerBeforeRecovery.BreakoutRoomId);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(WorkspaceRuntime)).Returns(_runtime);

        var orchestrator = new AgentOrchestrator(
            scopeFactory,
            Substitute.For<IAgentExecutor>(),
            new ActivityBroadcaster(),
            new SpecManager(),
            new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance),
            new GitService(NullLogger<GitService>.Instance),
            NullLogger<AgentOrchestrator>.Instance);

        await orchestrator.HandleStartupRecoveryAsync("main");

        var breakout = await _db.BreakoutRooms.FindAsync("breakout-1");
        Assert.NotNull(breakout);
        Assert.Equal(nameof(RoomStatus.Archived), breakout.Status);
        Assert.Equal(nameof(BreakoutRoomCloseReason.ClosedByRecovery), breakout.CloseReason);

        var engineer = await _db.AgentLocations.FindAsync("engineer-1");
        Assert.NotNull(engineer);
        Assert.Equal(nameof(AgentState.Idle), engineer.State);
        Assert.Null(engineer.BreakoutRoomId);

        var planner = await _db.AgentLocations.FindAsync("planner-1");
        Assert.NotNull(planner);
        Assert.Equal(nameof(AgentState.Idle), planner.State);
        Assert.Null(planner.BreakoutRoomId);

        var recoveryMessage = await _db.Messages
            .Where(m => m.RoomId == "main")
            .OrderByDescending(m => m.SentAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(recoveryMessage);
        Assert.Contains("System recovered from crash", recoveryMessage.Content);
    }

    // ── Queue Reconstruction ────────────────────────────────────

    [Fact]
    public async Task GetRoomsWithPendingHumanMessages_ReturnsRoom_WhenLatestMessageIsFromUser()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "main", Name = "Main Room",
            Status = nameof(RoomStatus.Idle), CurrentPhase = "Intake",
            CreatedAt = now, UpdatedAt = now
        });

        _db.Messages.Add(new MessageEntity
        {
            Id = "m1", RoomId = "main", SenderId = "human", SenderName = "Human",
            SenderKind = nameof(MessageSenderKind.User), Kind = nameof(MessageKind.Response),
            Content = "Hello", SentAt = now
        });

        await _db.SaveChangesAsync();

        var result = await _runtime.GetRoomsWithPendingHumanMessagesAsync();

        Assert.Single(result);
        Assert.Equal("main", result[0]);
    }

    [Fact]
    public async Task GetRoomsWithPendingHumanMessages_ExcludesRoom_WhenLatestMessageIsFromAgent()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "main", Name = "Main Room",
            Status = nameof(RoomStatus.Active), CurrentPhase = "Intake",
            CreatedAt = now, UpdatedAt = now
        });

        _db.Messages.AddRange(
            new MessageEntity
            {
                Id = "m1", RoomId = "main", SenderId = "human", SenderName = "Human",
                SenderKind = nameof(MessageSenderKind.User), Kind = nameof(MessageKind.Response),
                Content = "Hello", SentAt = now.AddMinutes(-5)
            },
            new MessageEntity
            {
                Id = "m2", RoomId = "main", SenderId = "agent-1", SenderName = "Agent",
                SenderKind = nameof(MessageSenderKind.Agent), Kind = nameof(MessageKind.Coordination),
                Content = "I'll handle this.", SentAt = now
            });

        await _db.SaveChangesAsync();

        var result = await _runtime.GetRoomsWithPendingHumanMessagesAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRoomsWithPendingHumanMessages_ExcludesArchivedAndCompletedRooms()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.AddRange(
            new RoomEntity
            {
                Id = "archived-room", Name = "Archived",
                Status = nameof(RoomStatus.Archived), CurrentPhase = "Intake",
                CreatedAt = now, UpdatedAt = now
            },
            new RoomEntity
            {
                Id = "completed-room", Name = "Completed",
                Status = nameof(RoomStatus.Completed), CurrentPhase = "Intake",
                CreatedAt = now, UpdatedAt = now
            });

        _db.Messages.AddRange(
            new MessageEntity
            {
                Id = "m1", RoomId = "archived-room", SenderId = "human", SenderName = "Human",
                SenderKind = nameof(MessageSenderKind.User), Kind = nameof(MessageKind.Response),
                Content = "Hello", SentAt = now
            },
            new MessageEntity
            {
                Id = "m2", RoomId = "completed-room", SenderId = "human", SenderName = "Human",
                SenderKind = nameof(MessageSenderKind.User), Kind = nameof(MessageKind.Response),
                Content = "Hello", SentAt = now
            });

        await _db.SaveChangesAsync();

        var result = await _runtime.GetRoomsWithPendingHumanMessagesAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRoomsWithPendingHumanMessages_ExcludesEmptyRooms()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "empty-room", Name = "Empty",
            Status = nameof(RoomStatus.Idle), CurrentPhase = "Intake",
            CreatedAt = now, UpdatedAt = now
        });

        await _db.SaveChangesAsync();

        var result = await _runtime.GetRoomsWithPendingHumanMessagesAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRoomsWithPendingHumanMessages_ReturnsMultipleRooms()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.AddRange(
            new RoomEntity
            {
                Id = "room-a", Name = "Room A",
                Status = nameof(RoomStatus.Idle), CurrentPhase = "Intake",
                CreatedAt = now, UpdatedAt = now
            },
            new RoomEntity
            {
                Id = "room-b", Name = "Room B",
                Status = nameof(RoomStatus.Active), CurrentPhase = "Intake",
                CreatedAt = now, UpdatedAt = now
            });

        _db.Messages.AddRange(
            new MessageEntity
            {
                Id = "m1", RoomId = "room-a", SenderId = "human", SenderName = "Human",
                SenderKind = nameof(MessageSenderKind.User), Kind = nameof(MessageKind.Response),
                Content = "Hello A", SentAt = now
            },
            new MessageEntity
            {
                Id = "m2", RoomId = "room-b", SenderId = "user-42", SenderName = "Bob",
                SenderKind = nameof(MessageSenderKind.User), Kind = nameof(MessageKind.Response),
                Content = "Hello B", SentAt = now
            });

        await _db.SaveChangesAsync();

        var result = await _runtime.GetRoomsWithPendingHumanMessagesAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains("room-a", result);
        Assert.Contains("room-b", result);
    }

    [Fact]
    public async Task GetRoomsWithPendingHumanMessages_ExcludesRoom_WhenLatestMessageIsSystem()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "main", Name = "Main Room",
            Status = nameof(RoomStatus.Idle), CurrentPhase = "Intake",
            CreatedAt = now, UpdatedAt = now
        });

        _db.Messages.AddRange(
            new MessageEntity
            {
                Id = "m1", RoomId = "main", SenderId = "human", SenderName = "Human",
                SenderKind = nameof(MessageSenderKind.User), Kind = nameof(MessageKind.Response),
                Content = "Hello", SentAt = now.AddMinutes(-5)
            },
            new MessageEntity
            {
                Id = "m2", RoomId = "main", SenderId = "system", SenderName = "System",
                SenderKind = nameof(MessageSenderKind.System), Kind = nameof(MessageKind.System),
                Content = "Recovery notice", SentAt = now
            });

        await _db.SaveChangesAsync();

        var result = await _runtime.GetRoomsWithPendingHumanMessagesAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ReconstructQueueAsync_EnqueuesRoomsWithPendingMessages()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "main", Name = "Main Room",
            Status = nameof(RoomStatus.Idle), CurrentPhase = "Intake",
            CreatedAt = now, UpdatedAt = now
        });

        _db.Messages.Add(new MessageEntity
        {
            Id = "m1", RoomId = "main", SenderId = "human", SenderName = "Human",
            SenderKind = nameof(MessageSenderKind.User), Kind = nameof(MessageKind.Response),
            Content = "Please process this", SentAt = now
        });

        await _db.SaveChangesAsync();

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(WorkspaceRuntime)).Returns(_runtime);

        // Use a stopped orchestrator so ProcessQueueAsync doesn't drain the queue
        var orchestrator = new AgentOrchestrator(
            scopeFactory,
            Substitute.For<IAgentExecutor>(),
            new ActivityBroadcaster(),
            new SpecManager(),
            new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance),
            new GitService(NullLogger<GitService>.Instance),
            NullLogger<AgentOrchestrator>.Instance);
        orchestrator.Stop();

        Assert.Equal(0, orchestrator.QueueDepth);

        await orchestrator.ReconstructQueueAsync();

        Assert.Equal(1, orchestrator.QueueDepth);
    }

    [Fact]
    public async Task ReconstructQueueAsync_NoOp_WhenNoPendingMessages()
    {
        var now = DateTime.UtcNow;
        _db.Rooms.Add(new RoomEntity
        {
            Id = "main", Name = "Main Room",
            Status = nameof(RoomStatus.Idle), CurrentPhase = "Intake",
            CreatedAt = now, UpdatedAt = now
        });

        _db.Messages.AddRange(
            new MessageEntity
            {
                Id = "m1", RoomId = "main", SenderId = "human", SenderName = "Human",
                SenderKind = nameof(MessageSenderKind.User), Kind = nameof(MessageKind.Response),
                Content = "Hello", SentAt = now.AddMinutes(-5)
            },
            new MessageEntity
            {
                Id = "m2", RoomId = "main", SenderId = "agent-1", SenderName = "Aristotle",
                SenderKind = nameof(MessageSenderKind.Agent), Kind = nameof(MessageKind.Coordination),
                Content = "I'll handle this.", SentAt = now
            });

        await _db.SaveChangesAsync();

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(WorkspaceRuntime)).Returns(_runtime);

        var orchestrator = new AgentOrchestrator(
            scopeFactory,
            Substitute.For<IAgentExecutor>(),
            new ActivityBroadcaster(),
            new SpecManager(),
            new CommandPipeline(Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance),
            new GitService(NullLogger<GitService>.Instance),
            NullLogger<AgentOrchestrator>.Instance);
        orchestrator.Stop();

        await orchestrator.ReconstructQueueAsync();

        Assert.Equal(0, orchestrator.QueueDepth);
    }
}

public class ServerInstanceEntityTests
{
    [Fact]
    public void NewEntity_HasDefaults()
    {
        var entity = new ServerInstanceEntity();

        Assert.NotNull(entity.Id);
        Assert.NotEmpty(entity.Id);
        Assert.True(entity.StartedAt <= DateTime.UtcNow);
        Assert.Null(entity.ShutdownAt);
        Assert.Null(entity.ExitCode);
        Assert.False(entity.CrashDetected);
        Assert.Equal("", entity.Version);
    }

    [Fact]
    public void NewEntity_GeneratesUniqueIds()
    {
        var a = new ServerInstanceEntity();
        var b = new ServerInstanceEntity();
        Assert.NotEqual(a.Id, b.Id);
    }
}

public class InstanceHealthResultTests
{
    [Fact]
    public void InstanceHealthResult_HasCorrectShape()
    {
        var result = new InstanceHealthResult(
            InstanceId: "test-123",
            StartedAt: DateTime.UtcNow,
            Version: "1.0.0",
            CrashDetected: false,
            ExecutorOperational: true,
            AuthFailed: false);

        Assert.Equal("test-123", result.InstanceId);
        Assert.Equal("1.0.0", result.Version);
        Assert.False(result.CrashDetected);
        Assert.True(result.ExecutorOperational);
        Assert.False(result.AuthFailed);
    }
}
