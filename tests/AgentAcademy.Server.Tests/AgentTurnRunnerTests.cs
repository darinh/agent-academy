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
using NSubstitute.ExceptionExtensions;

namespace AgentAcademy.Server.Tests;

public class AgentTurnRunnerTests : IDisposable
{
    private readonly IAgentExecutor _executor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly AgentCatalogOptions _catalog;
    private readonly ActivityPublisher _activityPublisher;
    private readonly IMessageService _messageService;
    private readonly IAgentConfigService _configService;
    private readonly ConversationSessionService _sessionService;
    private readonly AgentMemoryLoader _memoryLoader;
    private readonly AgentTurnRunner _runner;

    private static AgentDefinition TestAgent(string id = "agent-1", string name = "TestAgent") =>
        new(Id: id, Name: name, Role: "SoftwareEngineer",
            Summary: "Test agent", StartupPrompt: "Go", Model: null,
            CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true);

    private static RoomSnapshot TestRoom(string id = "room-1", string name = "Test Room") =>
        new(Id: id, Name: name, Topic: null, Status: RoomStatus.Active,
            CurrentPhase: CollaborationPhase.Discussion, ActiveTask: null,
            Participants: [], RecentMessages: [],
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

    public AgentTurnRunnerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        _executor = Substitute.For<IAgentExecutor>();
        _catalog = new AgentCatalogOptions("main", "Main Room",
        [
            TestAgent("agent-1", "TestAgent"),
        ]);

        var activityBus = new ActivityBroadcaster();
        _activityPublisher = new ActivityPublisher(_db, activityBus);

        var settingsService = new SystemSettingsService(_db);
        _sessionService = new ConversationSessionService(
            _db, settingsService, _executor,
            NullLogger<ConversationSessionService>.Instance);

        _messageService = new MessageService(
            _db, NullLogger<MessageService>.Instance, _catalog,
            _activityPublisher, _sessionService, new MessageBroadcaster());

        _configService = new AgentConfigService(_db);

        // Set up scope factory to return a scope whose ServiceProvider resolves the DB context
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(AgentAcademyDbContext)).Returns(_db);
        scope.ServiceProvider.Returns(serviceProvider);
        _scopeFactory.CreateScope().Returns(scope);

        _memoryLoader = new AgentMemoryLoader(
            _scopeFactory, NullLogger<AgentMemoryLoader>.Instance);

        var pipeline = new CommandPipeline(
            Array.Empty<ICommandHandler>(), NullLogger<CommandPipeline>.Instance);

        _runner = new AgentTurnRunner(
            _executor,
            pipeline,
            null!, // TaskAssignmentHandler — tested paths with task assignments will NRE but are caught
            _memoryLoader,
            _scopeFactory,
            NullLogger<AgentTurnRunner>.Instance, new TestDoubles.NoOpAgentLivenessTracker());

        // Seed default room so FK constraints pass for ActivityPublisher
        _db.Rooms.Add(new RoomEntity
        {
            Id = "room-1",
            Name = "Test Room",
            Topic = "",
            Status = RoomStatus.Active.ToString(),
            CurrentPhase = CollaborationPhase.Discussion.ToString(),
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

    private IServiceScope CreateMockScope()
    {
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(Substitute.For<IServiceProvider>());
        return scope;
    }

    private async Task SeedRoomAsync(string roomId)
    {
        if (await _db.Rooms.FindAsync(roomId) != null) return;
        _db.Rooms.Add(new RoomEntity
        {
            Id = roomId,
            Name = "Test Room",
            Topic = "",
            Status = RoomStatus.Active.ToString(),
            CurrentPhase = CollaborationPhase.Discussion.ToString(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    // ── BASIC FLOW ──────────────────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_ReturnsCorrectAgent()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Hello world");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Equal(agent.Id, result.Agent.Id);
        Assert.Equal(agent.Name, result.Agent.Name);
    }

    [Fact]
    public async Task RunAgentTurnAsync_SubstantiveResponse_IsNonPassTrue()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("I have completed the implementation of the feature.");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.True(result.IsNonPass);
        Assert.Equal("I have completed the implementation of the feature.", result.Response);
    }

    [Fact]
    public async Task RunAgentTurnAsync_EmptyResponse_IsNonPassFalse()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.False(result.IsNonPass);
        Assert.Equal("", result.Response);
    }

    [Fact]
    public async Task RunAgentTurnAsync_PassResponse_IsNonPassFalse()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("PASS");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.False(result.IsNonPass);
    }

    [Fact]
    public async Task RunAgentTurnAsync_BracketPassResponse_IsNonPassFalse()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("[PASS]");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.False(result.IsNonPass);
    }

    [Fact]
    public async Task RunAgentTurnAsync_WhitespaceOnlyResponse_IsNonPassFalse()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("   ");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.False(result.IsNonPass);
    }

    [Fact]
    public async Task RunAgentTurnAsync_StubOfflineResponse_IsNonPassFalse()
    {
        var agent = TestAgent();
        var offlineResponse = "TestAgent is offline — the Copilot SDK is not connected";
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(offlineResponse);

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.False(result.IsNonPass);
        Assert.Equal(offlineResponse, result.Response);
    }

    [Fact]
    public async Task RunAgentTurnAsync_NothingToAddResponse_IsNonPassFalse()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Nothing to add.");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.False(result.IsNonPass);
    }

    // ── EXCEPTION HANDLING (RunAgentAsync) ───────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_ExecutorThrowsGenericException_ReturnsEmptyResponse()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("something broke"));

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Equal("", result.Response);
        Assert.False(result.IsNonPass);
    }

    [Fact]
    public async Task RunAgentTurnAsync_ExecutorThrowsQuotaException_ReturnsWarningMessage()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new AgentQuotaExceededException("agent-1", "requests", "Rate limit hit", 60));

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Contains("TestAgent is temporarily paused", result.Response);
        Assert.Contains("Rate limit hit", result.Response);
        Assert.True(result.IsNonPass); // quota warning is a substantive response
    }

    [Fact]
    public async Task RunAgentTurnAsync_ExecutorThrowsOperationCanceled_ReturnsEmptyResponse()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new OperationCanceledException());

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Equal("", result.Response);
        Assert.False(result.IsNonPass);
    }

    [Fact]
    public async Task RunAgentTurnAsync_OuterCancellationRequested_PropagatesOCE()
    {
        var agent = TestAgent();
        // Executor honours the linked token: when the outer token is cancelled
        // it throws OCE bound to that token.
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                ct.ThrowIfCancellationRequested();
                return new InvalidOperationException("token was not cancelled");
            });

        using var outerCts = new CancellationTokenSource();
        outerCts.Cancel();

        var scope = CreateMockScope();
        await Assert.ThrowsAsync<OperationCanceledException>(() => _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null,
            cancellationToken: outerCts.Token));
    }

    // ── EFFECTIVE AGENT CONFIG ───────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_UsesEffectiveAgentFromConfigService()
    {
        var agent = TestAgent();

        // Add a config override in the DB
        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = "agent-1",
            ModelOverride = "gpt-5",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        _executor.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        // The returned agent should have the effective (overridden) model
        Assert.Equal("gpt-5", result.Agent.Model);
    }

    [Fact]
    public async Task RunAgentTurnAsync_NoConfigOverride_ReturnsCatalogAgent()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Null(result.Agent.Model);
        Assert.Equal("Go", result.Agent.StartupPrompt);
    }

    // ── ACTIVITY PUBLISHING ─────────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_PublishesThinkingAndFinishedActivities()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("response");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var activities = await _db.ActivityEvents
            .Where(a => a.ActorId == "agent-1")
            .OrderBy(a => a.OccurredAt)
            .ToListAsync();

        Assert.Contains(activities, a => a.Type == ActivityEventType.AgentThinking.ToString());
        Assert.Contains(activities, a => a.Type == ActivityEventType.AgentFinished.ToString());
    }

    [Fact]
    public async Task RunAgentTurnAsync_PublishesFinishedEvenOnException()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var finishedActivities = await _db.ActivityEvents
            .Where(a => a.ActorId == "agent-1" && a.Type == ActivityEventType.AgentFinished.ToString())
            .ToListAsync();

        Assert.NotEmpty(finishedActivities);
    }

    // ── PROMPT BUILDING ─────────────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_AppendsPromptSuffix()
    {
        var agent = TestAgent();
        string? capturedPrompt = null;
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                capturedPrompt = call.ArgAt<string>(1);
                return "";
            });

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null, promptSuffix: "\n\nDo extra work.");

        Assert.NotNull(capturedPrompt);
        Assert.EndsWith("\n\nDo extra work.", capturedPrompt);
    }

    [Fact]
    public async Task RunAgentTurnAsync_NullPromptSuffix_DoesNotAppend()
    {
        var agent = TestAgent();
        string? capturedPrompt = null;
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                capturedPrompt = call.ArgAt<string>(1);
                return "";
            });

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null, promptSuffix: null);

        Assert.NotNull(capturedPrompt);
        Assert.DoesNotContain("Do extra work", capturedPrompt);
    }

    [Fact]
    public async Task RunAgentTurnAsync_PassesSpecContextToPrompt()
    {
        var agent = TestAgent();
        string? capturedPrompt = null;
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                capturedPrompt = call.ArgAt<string>(1);
                return "";
            });

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", "## Spec Section 1\nDetails here");

        Assert.NotNull(capturedPrompt);
        Assert.Contains("Spec Section 1", capturedPrompt);
    }

    [Fact]
    public async Task RunAgentTurnAsync_PassesSessionSummaryToPrompt()
    {
        var agent = TestAgent();
        string? capturedPrompt = null;
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                capturedPrompt = call.ArgAt<string>(1);
                return "";
            });

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null, sessionSummary: "Previous discussion about auth.");

        Assert.NotNull(capturedPrompt);
        Assert.Contains("Previous discussion about auth.", capturedPrompt);
    }

    [Fact]
    public async Task RunAgentTurnAsync_PassesRoomIdToExecutor()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), "my-room-42", Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("ok");

        await SeedRoomAsync("my-room-42");
        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom("my-room-42"), "my-room-42", null);

        await _executor.Received(1)
            .RunAsync(agent, Arg.Any<string>(), "my-room-42", Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    // ── MEMORY LOADING ──────────────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_LoadsMemoriesForAgent()
    {
        var agent = TestAgent();

        // Seed a memory entry
        _db.AgentMemories.Add(new AgentMemoryEntity
        {
            AgentId = "agent-1",
            Category = "context",
            Key = "preference",
            Value = "uses tabs",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        string? capturedPrompt = null;
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                capturedPrompt = call.ArgAt<string>(1);
                return "";
            });

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.NotNull(capturedPrompt);
        // The PromptBuilder includes memory entries in the prompt
        Assert.Contains("uses tabs", capturedPrompt);
    }

    // ── DM LOADING ──────────────────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_LoadsUnreadDirectMessages()
    {
        var agent = TestAgent();

        // Seed a DM for the agent
        _db.Messages.Add(new MessageEntity
        {
            Id = "dm-1",
            RoomId = "room-1",
            SenderId = "human",
            SenderName = "Human",
            SenderKind = "User",
            Kind = "DirectMessage",
            Content = "Please focus on the auth module.",
            SentAt = DateTime.UtcNow,
            RecipientId = "agent-1"
        });
        await _db.SaveChangesAsync();

        string? capturedPrompt = null;
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                capturedPrompt = call.ArgAt<string>(1);
                return "";
            });

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("focus on the auth module", capturedPrompt);
    }

    [Fact]
    public async Task RunAgentTurnAsync_AcknowledgesDirectMessagesAfterLoading()
    {
        var agent = TestAgent();

        _db.Messages.Add(new MessageEntity
        {
            Id = "dm-ack-1",
            RoomId = "room-1",
            SenderId = "human",
            SenderName = "Human",
            SenderKind = "User",
            Kind = "DirectMessage",
            Content = "Test DM",
            SentAt = DateTime.UtcNow,
            RecipientId = "agent-1"
        });
        await _db.SaveChangesAsync();

        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var dm = await _db.Messages.FindAsync("dm-ack-1");
        Assert.NotNull(dm);
        Assert.NotNull(dm.AcknowledgedAt);
    }

    [Fact]
    public async Task RunAgentTurnAsync_NoDMs_DoesNotThrow()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.NotNull(result);
    }

    // ── NON-PASS RESPONSE PROCESSING ────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_NonPassResponse_PostsAgentMessage()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Here is my detailed analysis of the codebase.");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var agentMessages = await _db.Messages
            .Where(m => m.SenderId == "agent-1" && m.Kind == nameof(MessageKind.Response))
            .ToListAsync();

        Assert.NotEmpty(agentMessages);
        Assert.Contains(agentMessages, m => m.Content.Contains("detailed analysis"));
    }

    [Fact]
    public async Task RunAgentTurnAsync_PassResponse_DoesNotPostMessage()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("PASS");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var agentMessages = await _db.Messages
            .Where(m => m.SenderId == "agent-1")
            .ToListAsync();

        Assert.Empty(agentMessages);
    }

    [Fact]
    public async Task RunAgentTurnAsync_EmptyResponse_DoesNotPostMessage()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var agentMessages = await _db.Messages
            .Where(m => m.SenderId == "agent-1")
            .ToListAsync();

        Assert.Empty(agentMessages);
    }

    // ── COMMAND PIPELINE ────────────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_ResponseWithNoCommands_PostsFullText()
    {
        var agent = TestAgent();
        var response = "This is a plain response with no commands.";
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(response);

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var agentMessages = await _db.Messages
            .Where(m => m.SenderId == "agent-1" && m.Kind == nameof(MessageKind.Response))
            .ToListAsync();

        Assert.Single(agentMessages);
        Assert.Equal(response, agentMessages[0].Content);
    }

    // ── RETURN VALUE STRUCTURE ───────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_ReturnsAgentTurnResult()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("result text");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.IsType<AgentTurnResult>(result);
        Assert.Equal("result text", result.Response);
    }

    [Fact]
    public async Task RunAgentTurnAsync_AgentTurnResult_HasAllProperties()
    {
        var agent = TestAgent("my-agent", "MyAgent");
        _executor.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("done");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.NotNull(result.Agent);
        Assert.NotNull(result.Response);
        // IsNonPass is a bool — always has a value
    }

    // ── QUOTA EXCEPTION DETAILS ─────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_QuotaException_ResponseContainsWarningEmoji()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new AgentQuotaExceededException("agent-1", "tokens", "Token budget exhausted", 120));

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Contains("⚠️", result.Response);
    }

    [Fact]
    public async Task RunAgentTurnAsync_QuotaException_ResponseContainsAgentName()
    {
        var agent = TestAgent("agent-1", "Hephaestus");
        _executor.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new AgentQuotaExceededException("agent-1", "requests", "Too many requests", 60));

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Contains("Hephaestus", result.Response);
    }

    [Fact]
    public async Task RunAgentTurnAsync_QuotaException_ResponseContainsExceptionMessage()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new AgentQuotaExceededException("agent-1", "cost", "Cost limit exceeded for this hour", 300));

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Contains("Cost limit exceeded for this hour", result.Response);
    }

    // ── EXECUTOR INTERACTIONS ───────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_CallsExecutorExactlyOnce()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        await _executor.Received(1)
            .RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task RunAgentTurnAsync_DoesNotCallExecutorAfterException()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new Exception("fail"));

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        // Verify it was called exactly once (no retry)
        await _executor.Received(1)
            .RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    // ── EDGE CASES ──────────────────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_NullSpecContext_DoesNotThrow()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task RunAgentTurnAsync_AllOptionalParamsNull_DoesNotThrow()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null,
            taskItems: null, sessionSummary: null, sprintPreamble: null,
            promptSuffix: null, specVersion: null);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task RunAgentTurnAsync_WithTaskItems_IncludesInPrompt()
    {
        var agent = TestAgent();
        string? capturedPrompt = null;
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                capturedPrompt = call.ArgAt<string>(1);
                return "";
            });

        var taskItems = new List<TaskItem>
        {
            new("task-1", "Implement auth", "Build JWT auth module",
                TaskItemStatus.Active, "agent-1", "room-1", null, null, null,
                DateTime.UtcNow, DateTime.UtcNow)
        };

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null, taskItems: taskItems);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("Implement auth", capturedPrompt);
    }

    [Fact]
    public async Task RunAgentTurnAsync_WithSprintPreamble_IncludesInPrompt()
    {
        var agent = TestAgent();
        string? capturedPrompt = null;
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                capturedPrompt = call.ArgAt<string>(1);
                return "";
            });

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null, sprintPreamble: "Sprint Goal: Deliver MVP");

        Assert.NotNull(capturedPrompt);
        Assert.Contains("Sprint Goal: Deliver MVP", capturedPrompt);
    }

    // ── MESSAGE POSTING DETAILS ─────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_NonPass_PostsWithCorrectSenderId()
    {
        var agent = TestAgent("eng-42", "Hephaestus");
        _catalog.Agents.Add(agent);
        _executor.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Here is my detailed analysis of the situation.");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var msg = await _db.Messages
            .Where(m => m.SenderId == "eng-42")
            .FirstOrDefaultAsync();

        Assert.NotNull(msg);
        Assert.Equal("eng-42", msg.SenderId);
    }

    [Fact]
    public async Task RunAgentTurnAsync_NonPass_PostsWithCorrectRoomId()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Detailed response about the feature implementation plan.");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var msg = await _db.Messages
            .Where(m => m.SenderId == "agent-1" && m.Kind == nameof(MessageKind.Response))
            .FirstOrDefaultAsync();

        Assert.NotNull(msg);
        Assert.Equal("room-1", msg.RoomId);
    }

    [Fact]
    public async Task RunAgentTurnAsync_NonPass_PostsWithInferredMessageKind()
    {
        // Planner role should map to Coordination kind
        var plannerAgent = new AgentDefinition(
            Id: "planner-1", Name: "Aristotle", Role: "Planner",
            Summary: "Planner", StartupPrompt: "Plan", Model: null,
            CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true);
        _catalog.Agents.Add(plannerAgent);

        _executor.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("Here is the detailed plan for our implementation sprint.");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            plannerAgent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var msg = await _db.Messages
            .Where(m => m.SenderId == "planner-1")
            .FirstOrDefaultAsync();

        Assert.NotNull(msg);
        Assert.Equal(nameof(MessageKind.Coordination), msg.Kind);
    }

    // ── CONCURRENT / MULTIPLE CALLS ─────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_MultipleCalls_EachReturnsIndependently()
    {
        var agent = TestAgent();
        var callCount = 0;
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                callCount++;
                return callCount == 1 ? "first" : "PASS";
            });

        var scope = CreateMockScope();
        var result1 = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        var result2 = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.True(result1.IsNonPass);
        Assert.False(result2.IsNonPass);
    }

    // ── PASS RESPONSE VARIANTS ──────────────────────────────────

    [Theory]
    [InlineData("PASS")]
    [InlineData("[PASS]")]
    [InlineData("pass")]
    [InlineData("N/A")]
    [InlineData("No comment.")]
    [InlineData("Nothing to add.")]
    public async Task RunAgentTurnAsync_PassVariants_AllReturnNonPassFalse(string response)
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(response);

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.False(result.IsNonPass, $"Expected IsNonPass=false for response '{response}'");
    }

    [Theory]
    [InlineData("PASS")]
    [InlineData("[PASS]")]
    [InlineData("pass")]
    [InlineData("N/A")]
    [InlineData("No comment.")]
    [InlineData("Nothing to add.")]
    public async Task RunAgentTurnAsync_PassVariants_DoNotPostMessages(string response)
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(response);

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var msgs = await _db.Messages.Where(m => m.SenderId == "agent-1").ToListAsync();
        Assert.Empty(msgs);
    }

    // ── OFFLINE RESPONSES ───────────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_OfflineResponse_DoesNotPostMessage()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("TestAgent is offline — the Copilot SDK is not connected. Please try again later.");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var msgs = await _db.Messages.Where(m => m.SenderId == "agent-1").ToListAsync();
        Assert.Empty(msgs);
    }

    // ── CANCELLATION EXCEPTION ──────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_TaskCanceledException_TreatedAsCancellation()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new TaskCanceledException("Request timed out"));

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Equal("", result.Response);
        Assert.False(result.IsNonPass);
    }

    // ── CONFIG OVERRIDE WITH PROMPT ─────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_ConfigOverridePrompt_UsedByExecutor()
    {
        var agent = TestAgent();

        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = "agent-1",
            StartupPromptOverride = "Custom startup prompt for testing",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        AgentDefinition? capturedAgent = null;
        _executor.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                capturedAgent = call.ArgAt<AgentDefinition>(0);
                return "";
            });

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.NotNull(capturedAgent);
        Assert.Equal("Custom startup prompt for testing", capturedAgent.StartupPrompt);
    }

    // ── SPEC VERSION ────────────────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_WithSpecVersion_IncludesInPrompt()
    {
        var agent = TestAgent();
        string? capturedPrompt = null;
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call =>
            {
                capturedPrompt = call.ArgAt<string>(1);
                return "";
            });

        var scope = CreateMockScope();
        // specVersion is only rendered when specContext is also provided
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", "## Spec\nContent", specVersion: "2.3.1");

        Assert.NotNull(capturedPrompt);
        Assert.Contains("v2.3.1", capturedPrompt);
    }

    // ── ACTIVITY EVENT ORDERING ─────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_ThinkingPublishedBeforeFinished()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("ok");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        _db.ChangeTracker.Clear();
        var activities = await _db.ActivityEvents
            .Where(a => a.ActorId == "agent-1")
            .OrderBy(a => a.OccurredAt)
            .ToListAsync();

        var thinkingIdx = activities.FindIndex(a => a.Type == ActivityEventType.AgentThinking.ToString());
        var finishedIdx = activities.FindIndex(a => a.Type == ActivityEventType.AgentFinished.ToString());

        Assert.True(thinkingIdx >= 0, "Thinking activity not found");
        Assert.True(finishedIdx >= 0, "Finished activity not found");
        Assert.True(thinkingIdx < finishedIdx, "Thinking should be before Finished");
    }

    // ── LARGE RESPONSE ──────────────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_LargeResponse_IsNonPass()
    {
        var agent = TestAgent();
        var largeResponse = new string('x', 10_000);
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(largeResponse);

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.True(result.IsNonPass);
    }

    // ── EXECUTOR RECEIVES CORRECT ARGUMENTS ─────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_ExecutorReceivesNullWorkspacePath()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), "room-1", null, Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("");

        var scope = CreateMockScope();
        await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        await _executor.Received(1).RunAsync(
            agent, Arg.Any<string>(), "room-1", null, Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    // ── EXCEPTION IN OUTER CATCH ────────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_GenericExceptionInTryCatch_ReturnsGracefully()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new OutOfMemoryException("OOM"));

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Equal("", result.Response);
        Assert.False(result.IsNonPass);
    }

    [Fact]
    public async Task RunAgentTurnAsync_AggregateException_CaughtGracefully()
    {
        var agent = TestAgent();
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new AggregateException("Multiple failures",
                new InvalidOperationException("inner1"),
                new TimeoutException("inner2")));

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Equal("", result.Response);
        Assert.False(result.IsNonPass);
    }

    // ── RESPONSE PRESERVED EXACTLY ──────────────────────────────

    [Fact]
    public async Task RunAgentTurnAsync_ResponsePreservedExactly()
    {
        var agent = TestAgent();
        var response = "  Hello with leading spaces and\nnewlines\t\tand tabs  ";
        _executor.RunAsync(agent, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(response);

        var scope = CreateMockScope();
        var result = await _runner.RunAgentTurnAsync(
            agent, scope, _messageService, _configService, _activityPublisher,
            TestRoom(), "room-1", null);

        Assert.Equal(response, result.Response);
    }
}
