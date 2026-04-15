using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="ConversationKickoffService"/>: idempotent kickoff
/// message posting and orchestration triggering at startup.
/// </summary>
public sealed class ConversationKickoffTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentOrchestrator _orchestrator;
    private readonly string _mainRoomId = "main-room";

    private static readonly AgentCatalogOptions Catalog = new(
        DefaultRoomId: "main-room",
        DefaultRoomName: "Main Room",
        Agents:
        [
            new("agent-1", "Alpha", "Planner", "Planning lead", "You plan.",
                null, ["planning"], ["chat"], true)
        ]);

    public ConversationKickoffTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var executor = Substitute.For<IAgentExecutor>();
        executor.IsFullyOperational.Returns(true);
        executor.IsAuthFailed.Returns(false);
        executor.CircuitBreakerState.Returns(CircuitState.Closed);
        executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("PASS");

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<IAgentCatalog>(Catalog);
        var __broadcaster = new ActivityBroadcaster();
        services.AddSingleton(__broadcaster);
        services.AddSingleton<IActivityBroadcaster>(__broadcaster);
        var msgBroadcaster = new MessageBroadcaster();
        services.AddSingleton(msgBroadcaster);
        services.AddSingleton<IMessageBroadcaster>(msgBroadcaster);
        services.AddSingleton<IAgentExecutor>(executor);
        var specManager = new SpecManager(
            specsDir: Path.Combine(Path.GetTempPath(), $"kickoff-test-{Guid.NewGuid()}"),
            logger: NullLogger<SpecManager>.Instance);
        services.AddSingleton(specManager);
        services.AddDomainServices();
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();

        // Create DB schema and seed the room
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
            db.Rooms.Add(new RoomEntity
            {
                Id = _mainRoomId,
                Name = "Main Room",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        // Build orchestrator (singleton with all dependencies)
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var activityBus = _serviceProvider.GetRequiredService<ActivityBroadcaster>();
        var memoryLoader = new AgentMemoryLoader(scopeFactory, NullLogger<AgentMemoryLoader>.Instance);
        var pipeline = new CommandPipeline([], NullLogger<CommandPipeline>.Instance);
        var turnRunner = new AgentTurnRunner(
            executor, pipeline, null!, memoryLoader, scopeFactory,
            NullLogger<AgentTurnRunner>.Instance);
        var breakoutCompletion = new BreakoutCompletionService(
            scopeFactory, Catalog, executor, specManager,
            pipeline, memoryLoader,
            NullLogger<BreakoutCompletionService>.Instance);
        var gitService = new GitService(NullLogger<GitService>.Instance, repositoryRoot: "/tmp/fake-repo");
        var worktreeService = new WorktreeService(NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/fake-repo");
        var breakoutLifecycle = new BreakoutLifecycleService(
            scopeFactory, Catalog, executor, specManager,
            gitService, worktreeService, memoryLoader,
            breakoutCompletion,
            NullLogger<BreakoutLifecycleService>.Instance);
        _orchestrator = new AgentOrchestrator(
            scopeFactory,
            new ConversationRoundRunner(scopeFactory, Catalog, turnRunner, NullLogger<ConversationRoundRunner>.Instance),
            new DirectMessageRouter(scopeFactory, Catalog, turnRunner, NullLogger<DirectMessageRouter>.Instance),
            breakoutLifecycle,
            NullLogger<AgentOrchestrator>.Instance);
        _orchestrator.Stop(); // prevent async queue processing
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private ConversationKickoffService CreateSut()
    {
        var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        return new ConversationKickoffService(
            db, messageService, _orchestrator,
            NullLogger<ConversationKickoffService>.Instance);
    }

    // ──────────────── Kickoff triggers ────────────────

    [Fact]
    public async Task TryKickoff_EmptyRoom_PostsSystemMessage()
    {
        var sut = CreateSut();

        var result = await sut.TryKickoffAsync(_mainRoomId, activeWorkspace: null);

        Assert.True(result);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var messages = await db.Messages.Where(m => m.RoomId == _mainRoomId).ToListAsync();
        Assert.Single(messages);
        Assert.Equal(nameof(MessageSenderKind.System), messages[0].SenderKind);
        Assert.Contains("Team assembled", messages[0].Content);
    }

    [Fact]
    public async Task TryKickoff_EmptyRoom_NoWorkspace_UsesOnboardMessage()
    {
        var sut = CreateSut();

        await sut.TryKickoffAsync(_mainRoomId, activeWorkspace: null);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var msg = await db.Messages.SingleAsync(m => m.RoomId == _mainRoomId);
        Assert.Contains("No workspace is active", msg.Content);
        Assert.Contains("onboard a project", msg.Content);
    }

    [Fact]
    public async Task TryKickoff_EmptyRoom_WithWorkspace_UsesWorkspaceMessage()
    {
        var sut = CreateSut();

        await sut.TryKickoffAsync(_mainRoomId, activeWorkspace: "/home/dev/my-project");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var msg = await db.Messages.SingleAsync(m => m.RoomId == _mainRoomId);
        Assert.Contains("Workspace ready", msg.Content);
        Assert.Contains("/home/dev/my-project", msg.Content);
        Assert.Contains("Aristotle", msg.Content);
    }

    [Fact]
    public async Task TryKickoff_EmptyRoom_TriggersOrchestration()
    {
        var sut = CreateSut();

        await sut.TryKickoffAsync(_mainRoomId, activeWorkspace: null);

        Assert.True(_orchestrator.QueueDepth >= 1,
            "Expected orchestrator queue to have at least 1 item after kickoff");
    }

    // ──────────────── Idempotency ────────────────

    [Fact]
    public async Task TryKickoff_WithExistingAgentMessage_Skips()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RoomId = _mainRoomId,
                SenderId = "agent-1",
                SenderName = "Alpha",
                SenderKind = nameof(MessageSenderKind.Agent),
                Kind = nameof(MessageKind.Response),
                Content = "I'm ready to work.",
                SentAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.TryKickoffAsync(_mainRoomId, activeWorkspace: null);

        Assert.False(result);

        using var checkScope = _serviceProvider.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var msgCount = await checkDb.Messages.CountAsync(m => m.RoomId == _mainRoomId);
        Assert.Equal(1, msgCount);
    }

    [Fact]
    public async Task TryKickoff_WithExistingUserMessage_Skips()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RoomId = _mainRoomId,
                SenderId = "human",
                SenderName = "Human",
                SenderKind = nameof(MessageSenderKind.User),
                Kind = nameof(MessageKind.Response),
                Content = "Hello team!",
                SentAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.TryKickoffAsync(_mainRoomId, activeWorkspace: null);

        Assert.False(result);
    }

    [Fact]
    public async Task TryKickoff_WithOnlySystemMessages_StillKicksOff()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RoomId = _mainRoomId,
                SenderId = "system",
                SenderName = "System",
                SenderKind = nameof(MessageSenderKind.System),
                Kind = nameof(MessageKind.System),
                Content = "Server started.",
                SentAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.TryKickoffAsync(_mainRoomId, activeWorkspace: null);

        Assert.True(result);
    }

    [Fact]
    public async Task TryKickoff_CalledTwice_SecondAlsoFires_UntilAgentSpeaks()
    {
        var sut = CreateSut();

        var first = await sut.TryKickoffAsync(_mainRoomId, activeWorkspace: null);
        Assert.True(first);

        // The kickoff posts a System message, which doesn't prevent subsequent kickoffs.
        // In production, the InitializeAsync guard (CrashRecoveryService check + only
        // called once per startup) provides the outer idempotency guarantee.
        var second = await sut.TryKickoffAsync(_mainRoomId, activeWorkspace: null);
        Assert.True(second, "Without agent messages, second kickoff also fires (expected)");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var count = await db.Messages.CountAsync(m =>
            m.RoomId == _mainRoomId && m.SenderKind == nameof(MessageSenderKind.System));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task TryKickoff_DifferentRoom_DoesNotAffectMainRoom()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Rooms.Add(new RoomEntity
            {
                Id = "other-room",
                Name = "Other Room",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.Messages.Add(new MessageEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RoomId = "other-room",
                SenderId = "agent-1",
                SenderName = "Alpha",
                SenderKind = nameof(MessageSenderKind.Agent),
                Kind = nameof(MessageKind.Response),
                Content = "Working on it.",
                SentAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.TryKickoffAsync(_mainRoomId, activeWorkspace: null);

        Assert.True(result, "Agent messages in other rooms should not prevent main room kickoff");
    }
}
