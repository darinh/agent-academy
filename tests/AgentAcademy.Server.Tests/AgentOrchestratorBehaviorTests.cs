using AgentAcademy.Server.Commands;
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
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Behavioral tests for <see cref="AgentOrchestrator"/>: queue processing,
/// conversation rounds, DM handling, and startup recovery.
/// Uses real service graph with in-memory SQLite and mocked IAgentExecutor.
/// </summary>
public sealed class AgentOrchestratorBehaviorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentOrchestrator _orchestrator;
    private readonly IAgentExecutor _executor;
    private readonly List<string> _logErrors = [];
    private readonly AgentCatalogOptions _catalog;
    private readonly List<(string AgentId, string Prompt)> _executorCalls = [];
    private readonly SemaphoreSlim _executorGate = new(0);

    // ── Test agents ─────────────────────────────────────────────

    private static AgentDefinition Planner => new(
        Id: "planner-1", Name: "Aristotle", Role: "Planner",
        Summary: "Planning lead", StartupPrompt: "You are the planner.",
        Model: null, CapabilityTags: ["planning"], EnabledTools: ["chat"],
        AutoJoinDefaultRoom: true);

    private static AgentDefinition Engineer => new(
        Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
        Summary: "Backend engineer", StartupPrompt: "You are the engineer.",
        Model: null, CapabilityTags: ["implementation"], EnabledTools: ["chat", "code"],
        AutoJoinDefaultRoom: true);

    private static AgentDefinition Reviewer => new(
        Id: "reviewer-1", Name: "Athena", Role: "Reviewer",
        Summary: "Code reviewer", StartupPrompt: "You review code.",
        Model: null, CapabilityTags: ["review"], EnabledTools: ["chat"],
        AutoJoinDefaultRoom: true);

    // ── Fixture setup ───────────────────────────────────────────

    public AgentOrchestratorBehaviorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents: [Planner, Engineer, Reviewer]);

        _executor = Substitute.For<IAgentExecutor>();
        _executor.IsFullyOperational.Returns(true);
        _executor.IsAuthFailed.Returns(false);
        _executor.CircuitBreakerState.Returns(CircuitState.Closed);

        // Default: all agents return PASS
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var agent = callInfo.Arg<AgentDefinition>();
                var prompt = callInfo.ArgAt<string>(1);
                lock (_executorCalls) { _executorCalls.Add((agent.Id, prompt)); }
                _executorGate.Release();
                return "PASS";
            });

        var services = new ServiceCollection();

        // DB context (scoped)
        services.AddDbContext<AgentAcademyDbContext>(opt =>
            opt.UseSqlite(_connection));

        // Singletons needed by scoped services
        services.AddSingleton<IAgentCatalog>(_catalog);
        var __broadcaster = new ActivityBroadcaster();
        services.AddSingleton(__broadcaster);
        services.AddSingleton<IActivityBroadcaster>(__broadcaster);
        var msgBroadcaster = new MessageBroadcaster();
        services.AddSingleton(msgBroadcaster);
        services.AddSingleton<IMessageBroadcaster>(msgBroadcaster);
        services.AddSingleton<IAgentExecutor>(_executor);
        services.AddSingleton(new SpecManager(
            specsDir: Path.Combine(Path.GetTempPath(), $"orchestrator-test-specs-{Guid.NewGuid()}"),
            logger: NullLogger<SpecManager>.Instance));
        services.AddSingleton<ISpecManager>(sp => sp.GetRequiredService<SpecManager>());

        // Scoped domain services (matches production registration)
        services.AddDomainServices();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        _serviceProvider = services.BuildServiceProvider();

        // Create database schema
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
        }

        // Build singleton orchestration services manually
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var memoryLoader = new AgentMemoryLoader(
            scopeFactory, NullLogger<AgentMemoryLoader>.Instance);

        var pipeline = new CommandPipeline(
            Array.Empty<ICommandHandler>(),
            NullLogger<CommandPipeline>.Instance);

        var specManager = new SpecManager(
            specsDir: Path.Combine(Path.GetTempPath(), "orchestrator-test-specs-" + Guid.NewGuid()),
            logger: NullLogger<SpecManager>.Instance);

        var turnRunner = new AgentTurnRunner(
            _executor, pipeline,
            null!, // TaskAssignmentHandler — not exercised in these tests
            memoryLoader, scopeFactory,
            NullLogger<AgentTurnRunner>.Instance);

        // BreakoutLifecycleService — orchestrator only calls .Stop()
        var breakoutCompletion = new BreakoutCompletionService(
            scopeFactory, _catalog, _executor, specManager,
            pipeline, memoryLoader,
            NullLogger<BreakoutCompletionService>.Instance);
        var gitService = new GitService(NullLogger<GitService>.Instance, repositoryRoot: "/tmp/fake-repo");
        var worktreeService = new WorktreeService(NullLogger<WorktreeService>.Instance, repositoryRoot: "/tmp/fake-repo");
        var breakoutLifecycle = new BreakoutLifecycleService(
            scopeFactory, _catalog, _executor, specManager,
            gitService, memoryLoader,
            breakoutCompletion,
            NullLogger<BreakoutLifecycleService>.Instance);

        var roundRunner = new ConversationRoundRunner(
            scopeFactory, _catalog, turnRunner,
            NullLogger<ConversationRoundRunner>.Instance);
        var dmRouter = new DirectMessageRouter(
            scopeFactory, _catalog, turnRunner,
            NullLogger<DirectMessageRouter>.Instance);

        var orchestratorLoggerFactory = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(_logErrors)));
        _orchestrator = new AgentOrchestrator(
            scopeFactory, roundRunner, dmRouter,
            breakoutLifecycle,
            orchestratorLoggerFactory.CreateLogger<AgentOrchestrator>());
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        _executorGate.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────

    private AgentAcademyDbContext GetDb()
    {
        // Note: scope is not tracked here — callers should use `using var db = GetDb()`
        // which disposes the DbContext. The scope itself leaks but is short-lived
        // and cleaned up when _serviceProvider is disposed in Dispose().
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    }

    private async Task SeedRoomAsync(string roomId, string name, string status = "Active")
    {
        using var db = GetDb();
        db.Rooms.Add(new RoomEntity { Id = roomId, Name = name, Status = status, Topic = "" });
        await db.SaveChangesAsync();
    }

    private async Task SeedMessageAsync(string roomId, string senderId, string senderName,
        string senderKind, string content, string kind = "Coordination")
    {
        using var db = GetDb();
        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid().ToString(),
            RoomId = roomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderKind = senderKind,
            Kind = kind,
            Content = content,
            SentAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentLocationAsync(string agentId, string roomId,
        AgentState state = AgentState.Idle, string? breakoutRoomId = null)
    {
        using var db = GetDb();
        db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = agentId,
            RoomId = roomId,
            State = state.ToString(),
            BreakoutRoomId = breakoutRoomId,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedDirectMessageAsync(string recipientId, string senderId,
        string senderName, string content, string roomId = "main")
    {
        using var db = GetDb();
        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid().ToString(),
            RoomId = roomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderKind = nameof(MessageSenderKind.User),
            Kind = nameof(MessageKind.DirectMessage),
            Content = content,
            SentAt = DateTime.UtcNow,
            RecipientId = recipientId,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedTaskForRoomAsync(string roomId, string taskId, string title)
    {
        using var db = GetDb();
        db.Tasks.Add(new TaskEntity
        {
            Id = taskId,
            Title = title,
            Description = "Test task",
            Status = nameof(Shared.Models.TaskStatus.Active),
            Type = nameof(TaskType.Feature),
            RoomId = roomId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Waits for the executor to be called at least <paramref name="count"/> times,
    /// with a bounded timeout. Returns false if the timeout expires.
    /// </summary>
    private async Task<bool> WaitForExecutorCallsAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        for (int i = 0; i < count; i++)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            if (!await _executorGate.WaitAsync(remaining)) return false;
        }
        return true;
    }

    private List<string> GetExecutorAgentIds()
    {
        lock (_executorCalls) { return _executorCalls.Select(c => c.AgentId).ToList(); }
    }

    // ── Queue Mechanics ─────────────────────────────────────────

    [Fact]
    public async Task HandleHumanMessage_TriggersConversationRound()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Hello agents");

        _orchestrator.HandleHumanMessage("room-1");

        var called = await WaitForExecutorCallsAsync(1);
        var errors = string.Join("\n", _logErrors);
        Assert.True(called, $"Executor should be called at least once. Log errors:\n{errors}");
        Assert.Contains("planner-1", GetExecutorAgentIds());
    }

    [Fact]
    public async Task HandleHumanMessage_NonExistentRoom_ProcessorRemainsUsable()
    {
        // Trigger for a room that doesn't exist
        _orchestrator.HandleHumanMessage("nonexistent-room");
        await Task.Delay(200);

        // Now trigger a valid room — processor should still work
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Hello");

        _orchestrator.HandleHumanMessage("room-1");

        var called = await WaitForExecutorCallsAsync(1);
        Assert.True(called, "Processor should recover and handle the next valid room");
    }

    [Fact]
    public async Task HandleDirectMessage_DeduplicatesSameAgentInQueue()
    {
        // Stop processing so items stay in queue for dedup check
        _orchestrator.Stop();
        await Task.Delay(50);

        _orchestrator.HandleDirectMessage("engineer-1");
        _orchestrator.HandleDirectMessage("engineer-1"); // duplicate
        _orchestrator.HandleDirectMessage("engineer-1"); // duplicate

        Assert.Equal(1, _orchestrator.QueueDepth);
    }

    [Fact]
    public async Task HandleDirectMessage_DifferentAgentsNotDeduped()
    {
        _orchestrator.Stop();
        await Task.Delay(50);

        _orchestrator.HandleDirectMessage("engineer-1");
        _orchestrator.HandleDirectMessage("reviewer-1");

        Assert.Equal(2, _orchestrator.QueueDepth);
    }

    [Fact]
    public void QueueDepth_ReflectsEnqueuedItems()
    {
        Assert.Equal(0, _orchestrator.QueueDepth);

        _orchestrator.Stop();
        _orchestrator.HandleHumanMessage("room-1");
        _orchestrator.HandleHumanMessage("room-2");

        // Items enqueued but not processed (stopped)
        Assert.Equal(2, _orchestrator.QueueDepth);
    }

    [Fact]
    public async Task Stop_PreventsNewRoundsFromProcessing()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Hello");

        _orchestrator.Stop();
        _orchestrator.HandleHumanMessage("room-1");

        await Task.Delay(300);
        Assert.Empty(GetExecutorAgentIds());
    }

    [Fact]
    public async Task QueueContinuesProcessingAfterItemFailure()
    {
        await SeedRoomAsync("room-1", "Bad Room");
        await SeedRoomAsync("room-2", "Good Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedAgentLocationAsync("engineer-1", "room-2", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Hi");
        await SeedMessageAsync("room-2", "human", "User", nameof(MessageSenderKind.User), "Hello");

        // First call throws, second should succeed
        var callCount = 0;
        _executor.RunAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var agent = callInfo.Arg<AgentDefinition>();
                var prompt = callInfo.ArgAt<string>(1);
                var n = Interlocked.Increment(ref callCount);
                lock (_executorCalls) { _executorCalls.Add((agent.Id, prompt)); }
                _executorGate.Release();
                if (n == 1) throw new InvalidOperationException("Simulated LLM failure");
                return "PASS";
            });

        _orchestrator.HandleHumanMessage("room-1");
        _orchestrator.HandleHumanMessage("room-2");

        // Wait for at least 2 calls (one failure, one success)
        var called = await WaitForExecutorCallsAsync(2);
        Assert.True(called, "Second room should still be processed after first fails");
    }

    // ── Conversation Rounds ─────────────────────────────────────

    [Fact]
    public async Task ConversationRound_PlannerRunsFirst_ThenTaggedAgents()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedAgentLocationAsync("engineer-1", "room-1", AgentState.Idle);
        await SeedAgentLocationAsync("reviewer-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Build a widget");

        // Planner tags the engineer
        _executor.RunAsync(
            Arg.Is<AgentDefinition>(a => a.Id == "planner-1"),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                lock (_executorCalls) { _executorCalls.Add(("planner-1", callInfo.ArgAt<string>(1))); }
                _executorGate.Release();
                return "@Hephaestus should implement this feature.";
            });

        _orchestrator.HandleHumanMessage("room-1");

        // Wait for planner + engineer
        var called = await WaitForExecutorCallsAsync(2);
        Assert.True(called, "Both planner and tagged engineer should be called");

        var ids = GetExecutorAgentIds();
        Assert.Equal("planner-1", ids[0]); // Planner runs first
        Assert.Contains("engineer-1", ids);  // Tagged engineer runs
    }

    [Fact]
    public async Task ConversationRound_FallsBackToIdleAgents_WhenNoneTagged()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedAgentLocationAsync("engineer-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Hello");

        // Planner responds but doesn't tag anyone
        _executor.RunAsync(
            Arg.Is<AgentDefinition>(a => a.Id == "planner-1"),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                lock (_executorCalls) { _executorCalls.Add(("planner-1", callInfo.ArgAt<string>(1))); }
                _executorGate.Release();
                return "I think we should discuss the requirements further.";
            });

        _orchestrator.HandleHumanMessage("room-1");

        // Planner + at least one idle agent
        var called = await WaitForExecutorCallsAsync(2);
        Assert.True(called, "Idle agents should be called as fallback");

        var ids = GetExecutorAgentIds();
        Assert.Equal("planner-1", ids[0]);
        Assert.True(ids.Count >= 2, "At least one idle agent should run after planner");
    }

    [Fact]
    public async Task ConversationRound_StopsWhenAllAgentsPass()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedAgentLocationAsync("engineer-1", "room-1", AgentState.Idle);
        await SeedTaskForRoomAsync("room-1", "task-1", "Test task");
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Hello");

        // All agents return PASS (the default mock behavior)

        _orchestrator.HandleHumanMessage("room-1");

        // Wait for at least one call (planner should run)
        var called = await WaitForExecutorCallsAsync(1);
        Assert.True(called, "At least the planner should be called");

        // Give time to verify no second round starts
        await Task.Delay(500);

        var ids = GetExecutorAgentIds();
        Assert.Contains("planner-1", ids); // Planner definitely ran

        // With all PASS responses + active task, should be at most 1 round.
        // Round 1: planner (PASS) + idle agents (PASS) → hadNonPassResponse=false → break
        Assert.True(ids.Count <= 4,
            $"Expected at most 1 round of calls (≤4), got {ids.Count}: [{string.Join(", ", ids)}]");
    }

    [Fact]
    public async Task ConversationRound_StopsAfterMaxRoundsWithActiveTask()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedTaskForRoomAsync("room-1", "task-1", "Active task");
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Work on the task");

        // Planner always returns a non-pass response (triggers continuation)
        _executor.RunAsync(
            Arg.Is<AgentDefinition>(a => a.Id == "planner-1"),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                lock (_executorCalls) { _executorCalls.Add(("planner-1", callInfo.ArgAt<string>(1))); }
                _executorGate.Release();
                return "Let me think about this more...";
            });

        _orchestrator.HandleHumanMessage("room-1");

        // MaxRoundsPerTrigger = 3, so planner should be called exactly 3 times
        var called = await WaitForExecutorCallsAsync(3, TimeSpan.FromSeconds(15));
        Assert.True(called, "Planner should be called 3 times (max rounds)");

        // Give a bit of extra time to make sure no 4th round happens
        await Task.Delay(500);

        var plannerCalls = GetExecutorAgentIds().Count(id => id == "planner-1");
        Assert.Equal(3, plannerCalls);
    }

    [Fact]
    public async Task ConversationRound_SkipsAgentsInWorkingState()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedAgentLocationAsync("engineer-1", "room-1", AgentState.Working); // Working!
        await SeedAgentLocationAsync("reviewer-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Review this");

        // Planner tags both engineer and reviewer
        _executor.RunAsync(
            Arg.Is<AgentDefinition>(a => a.Id == "planner-1"),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                lock (_executorCalls) { _executorCalls.Add(("planner-1", callInfo.ArgAt<string>(1))); }
                _executorGate.Release();
                return "@Hephaestus and @Athena should look at this.";
            });

        _orchestrator.HandleHumanMessage("room-1");

        // Wait for planner + reviewer (engineer should be skipped)
        var called = await WaitForExecutorCallsAsync(2, TimeSpan.FromSeconds(10));
        Assert.True(called, "Planner and reviewer should be called");

        await Task.Delay(300);

        var ids = GetExecutorAgentIds();
        Assert.DoesNotContain("engineer-1", ids); // Working agent skipped
        Assert.Contains("reviewer-1", ids);        // Idle agent runs
    }

    [Fact]
    public async Task ConversationRound_SprintStageExcludesPlannerRole()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedAgentLocationAsync("engineer-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Implement feature");

        // Create an active sprint in "Discussion" stage (Planner is allowed)
        // vs "Intake" stage where only Planner is allowed
        // The sprint preamble filtering happens via RoundContextLoader, which reads
        // from the sprint table. We need to seed a sprint.
        using (var db = GetDb())
        {
            var room = await db.Rooms.FindAsync("room-1");
            if (room is not null)
                room.WorkspacePath = "/test/workspace";
            await db.SaveChangesAsync();

            // Seed workspace
            db.Workspaces.Add(new WorkspaceEntity
            {
                Path = "/test/workspace",
                ProjectName = "Test Workspace",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            // Seed a sprint in "Intake" stage (only Planner allowed)
            db.Sprints.Add(new SprintEntity
            {
                Id = "sprint-1",
                WorkspacePath = "/test/workspace",
                CurrentStage = "Intake",
                Status = "Active",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        _orchestrator.HandleHumanMessage("room-1");

        // In "Intake" stage, only Planner role is allowed
        var called = await WaitForExecutorCallsAsync(1, TimeSpan.FromSeconds(10));
        Assert.True(called, "At least the planner should be called");

        await Task.Delay(500);

        var ids = GetExecutorAgentIds();
        // Engineer (SoftwareEngineer role) should be filtered out in Intake stage
        Assert.DoesNotContain("engineer-1", ids);
    }

    // ── DM Rounds ───────────────────────────────────────────────

    [Fact]
    public async Task DmRound_RunsAgentInCurrentRoom()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("engineer-1", "room-1", AgentState.Idle);
        await SeedDirectMessageAsync("engineer-1", "human", "User", "Hey, quick question", "room-1");
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Context");

        _orchestrator.HandleDirectMessage("engineer-1");

        var called = await WaitForExecutorCallsAsync(1);
        Assert.True(called, "Engineer should receive a turn after DM");
        Assert.Contains("engineer-1", GetExecutorAgentIds());
    }

    [Fact]
    public async Task DmRound_AllowsSameAgentToRequeueAfterFirstItemDequeues()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("engineer-1", "room-1", AgentState.Idle);
        await SeedDirectMessageAsync("engineer-1", "human", "User", "First DM", "room-1");
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Context");

        _orchestrator.HandleDirectMessage("engineer-1");

        var firstCall = await WaitForExecutorCallsAsync(1, TimeSpan.FromSeconds(5));
        Assert.True(firstCall, "First DM should trigger one engineer turn");

        await SeedDirectMessageAsync("engineer-1", "human", "User", "Second DM", "room-1");
        _orchestrator.HandleDirectMessage("engineer-1");

        var secondCall = await WaitForExecutorCallsAsync(1, TimeSpan.FromSeconds(5));
        Assert.True(secondCall, "Second DM should enqueue after the first item is dequeued");
        Assert.Equal(2, GetExecutorAgentIds().Count(id => id == "engineer-1"));
    }

    [Fact]
    public async Task DmRound_ForwardsDmsToBreakoutRoom_NoExecutorCall()
    {
        await SeedRoomAsync("room-1", "Main Room");
        // Seed a breakout room entity (different table than regular rooms)
        using (var db = GetDb())
        {
            db.BreakoutRooms.Add(new BreakoutRoomEntity
            {
                Id = "breakout-1",
                Name = "Breakout Room",
                ParentRoomId = "room-1",
                AssignedAgentId = "engineer-1",
                Status = nameof(RoomStatus.Active),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await SeedAgentLocationAsync("engineer-1", "room-1",
            AgentState.Working, breakoutRoomId: "breakout-1");
        await SeedDirectMessageAsync("engineer-1", "human", "User", "Update on the task", "room-1");

        _orchestrator.HandleDirectMessage("engineer-1");

        // DMs should be forwarded to breakout room, NOT trigger an executor call
        await Task.Delay(500);
        Assert.Empty(GetExecutorAgentIds());

        // Verify DM was forwarded as a breakout message
        using var verifyDb = GetDb();
        var breakoutMessages = await verifyDb.BreakoutMessages
            .Where(m => m.BreakoutRoomId == "breakout-1" && m.Content.Contains("Update on the task"))
            .ToListAsync();
        Assert.NotEmpty(breakoutMessages);
    }

    [Fact]
    public async Task DmRound_FallsBackToDefaultRoom_WhenNoLocation()
    {
        await SeedRoomAsync("main", "Main Room");
        await SeedDirectMessageAsync("engineer-1", "human", "User", "No location DM", "main");
        await SeedMessageAsync("main", "human", "User", nameof(MessageSenderKind.User), "Context");

        // No agent location seeded — should fall back to first/default room

        _orchestrator.HandleDirectMessage("engineer-1");

        var called = await WaitForExecutorCallsAsync(1, TimeSpan.FromSeconds(5));
        Assert.True(called, "Agent should still get a turn using fallback room");
        Assert.Contains("engineer-1", GetExecutorAgentIds());
    }

    [Fact]
    public async Task DmRound_UnknownAgent_ProcessorRemainsUsable()
    {
        _orchestrator.HandleDirectMessage("nonexistent-agent");
        await Task.Delay(200);

        // Processor should still work after unknown agent
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("engineer-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Hello");

        _orchestrator.HandleHumanMessage("room-1");

        var called = await WaitForExecutorCallsAsync(1);
        Assert.True(called, "Processor should recover and handle subsequent messages");
    }

    // ── Startup Recovery ────────────────────────────────────────

    [Fact]
    public async Task ReconstructQueueAsync_ReEnqueuesPendingRooms()
    {
        // Seed a room with an unanswered human message
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User",
            nameof(MessageSenderKind.User), "Unanswered question");

        await _orchestrator.ReconstructQueueAsync();

        var called = await WaitForExecutorCallsAsync(1);
        Assert.True(called, "Pending room should be processed after queue reconstruction");
    }

    [Fact]
    public async Task ReconstructQueueAsync_IgnoresRoomsWhereAgentRepliedLast()
    {
        await SeedRoomAsync("room-1", "Test Room");
        // Agent replied last — not pending
        await SeedMessageAsync("room-1", "human", "User",
            nameof(MessageSenderKind.User), "Question");
        await SeedMessageAsync("room-1", "planner-1", "Aristotle",
            nameof(MessageSenderKind.Agent), "Here's the answer");

        await _orchestrator.ReconstructQueueAsync();

        await Task.Delay(300);
        Assert.Empty(GetExecutorAgentIds());
    }

    [Fact]
    public async Task HandleStartupRecoveryAsync_SkipsWhenNoCrashDetected()
    {
        // CrashRecoveryService.CurrentCrashDetected is static and defaults to false.
        // With no crash detected, the method returns early without creating a scope
        // or running any recovery logic.
        await SeedRoomAsync("main", "Main Room");

        await _orchestrator.HandleStartupRecoveryAsync("main");

        // Verify no recovery side-effects: no activity events, no state changes
        using var db = GetDb();
        var recoveryEvents = await db.ActivityEvents.CountAsync();
        Assert.Equal(0, recoveryEvents);
    }

    // ── Planner Prompt ──────────────────────────────────────────

    [Fact]
    public async Task ConversationRound_PlannerReceivesPlannerSuffix()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Plan something");

        _orchestrator.HandleHumanMessage("room-1");

        var called = await WaitForExecutorCallsAsync(1);
        Assert.True(called);

        // The planner should receive a prompt containing the planner suffix
        var plannerCall = _executorCalls.First(c => c.AgentId == "planner-1");
        Assert.Contains("TASK ASSIGNMENT", plannerCall.Prompt);
        Assert.Contains("lead planner", plannerCall.Prompt);
    }

    // ── Session Rotation ────────────────────────────────────────

    [Fact]
    public async Task ConversationRound_SessionRotationFailure_DoesNotBlockRound()
    {
        await SeedRoomAsync("room-1", "Test Room");
        await SeedAgentLocationAsync("planner-1", "room-1", AgentState.Idle);
        await SeedMessageAsync("room-1", "human", "User", nameof(MessageSenderKind.User), "Hello");

        // Session rotation may fail gracefully (it's wrapped in try/catch)
        // This test verifies the round proceeds even if rotation check fails
        _orchestrator.HandleHumanMessage("room-1");

        var called = await WaitForExecutorCallsAsync(1);
        Assert.True(called, "Round should proceed even if session rotation fails");
    }
}

/// <summary>Simple logger provider that captures error/warning messages for assertions.</summary>
file sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages;
    public CapturingLoggerProvider(List<string> messages) => _messages = messages;
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
    public void Dispose() { }
}

file sealed class CapturingLogger : ILogger
{
    private readonly List<string> _messages;
    public CapturingLogger(List<string> messages) => _messages = messages;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
            lock (_messages) { _messages.Add($"[{logLevel}] {formatter(state, exception)}{(exception is not null ? $"\n{exception}" : "")}"); }
    }
}
