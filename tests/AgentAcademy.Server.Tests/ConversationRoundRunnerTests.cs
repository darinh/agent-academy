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
/// Unit tests for <see cref="ConversationRoundRunner"/>: round counting,
/// planner dispatch, agent selection, sprint filtering, and early termination.
/// Uses real DI container with in-memory SQLite and mocked IAgentTurnRunner.
/// </summary>
public sealed class ConversationRoundRunnerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IAgentTurnRunner _turnRunner;
    private readonly ConversationRoundRunner _runner;
    private readonly AgentCatalogOptions _catalog;
    private readonly List<(AgentDefinition Agent, string RoomId)> _turnCalls = [];
    private readonly object _turnLock = new();

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

    private static AgentDefinition Designer => new(
        Id: "designer-1", Name: "Apollo", Role: "Designer",
        Summary: "UI designer", StartupPrompt: "You design.",
        Model: null, CapabilityTags: ["design"], EnabledTools: ["chat"],
        AutoJoinDefaultRoom: true);

    private static AgentDefinition Writer => new(
        Id: "writer-1", Name: "Hermes", Role: "Writer",
        Summary: "Tech writer", StartupPrompt: "You write.",
        Model: null, CapabilityTags: ["docs"], EnabledTools: ["chat"],
        AutoJoinDefaultRoom: true);

    // ── Fixture setup ───────────────────────────────────────────

    public ConversationRoundRunnerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents: [Planner, Engineer, Reviewer, Designer, Writer]);

        _turnRunner = Substitute.For<IAgentTurnRunner>();

        // Default: all agents PASS
        _turnRunner.RunAgentTurnAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<IServiceScope>(),
            Arg.Any<IMessageService>(), Arg.Any<IAgentConfigService>(),
            Arg.Any<IActivityPublisher>(), Arg.Any<RoomSnapshot>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<TaskItem>?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>())
            .Returns(callInfo =>
            {
                var agent = callInfo.Arg<AgentDefinition>();
                var roomId = callInfo.ArgAt<string>(6);
                lock (_turnLock) { _turnCalls.Add((agent, roomId)); }
                return new AgentTurnResult(agent, "PASS", IsNonPass: false);
            });

        var services = new ServiceCollection();

        services.AddDbContext<AgentAcademyDbContext>(opt =>
            opt.UseSqlite(_connection));

        services.AddSingleton<IAgentCatalog>(_catalog);
        var broadcaster = new ActivityBroadcaster();
        services.AddSingleton(broadcaster);
        services.AddSingleton<IActivityBroadcaster>(broadcaster);
        var msgBroadcaster = new MessageBroadcaster();
        services.AddSingleton(msgBroadcaster);
        services.AddSingleton<IMessageBroadcaster>(msgBroadcaster);

        var executor = Substitute.For<IAgentExecutor>();
        executor.IsFullyOperational.Returns(true);
        executor.IsAuthFailed.Returns(false);
        executor.CircuitBreakerState.Returns(CircuitState.Closed);
        services.AddSingleton<IAgentExecutor>(executor);

        services.AddSingleton(new SpecManager(
            specsDir: Path.Combine(Path.GetTempPath(), $"roundrunner-test-specs-{Guid.NewGuid()}"),
            logger: NullLogger<SpecManager>.Instance));
        services.AddSingleton<ISpecManager>(sp => sp.GetRequiredService<SpecManager>());

        services.AddDomainServices();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Database.EnsureCreated();
        }

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _runner = new ConversationRoundRunner(
            scopeFactory, _catalog, _turnRunner,
            NullLogger<ConversationRoundRunner>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────

    private AgentAcademyDbContext CreateDb()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    }

    private async Task SeedRoomAsync(string roomId = "main", string name = "Main Room",
        string status = "Active", bool withActiveTask = false)
    {
        using var db = CreateDb();
        db.Rooms.Add(new RoomEntity
        {
            Id = roomId,
            Name = name,
            Status = status,
            Topic = "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        if (withActiveTask)
        {
            db.Tasks.Add(new TaskEntity
            {
                Id = $"task-{roomId}",
                Title = "Test task",
                Description = "A task for testing",
                Status = "Active",
                RoomId = roomId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedAgentLocationAsync(string agentId, string roomId,
        string state = "Idle", string? breakoutRoomId = null)
    {
        using var db = CreateDb();
        db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = agentId,
            RoomId = roomId,
            State = state,
            BreakoutRoomId = breakoutRoomId,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private void SetupTurnRunner(Func<AgentDefinition, AgentTurnResult> resultFactory)
    {
        _turnCalls.Clear();
        _turnRunner.RunAgentTurnAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<IServiceScope>(),
            Arg.Any<IMessageService>(), Arg.Any<IAgentConfigService>(),
            Arg.Any<IActivityPublisher>(), Arg.Any<RoomSnapshot>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<TaskItem>?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>())
            .Returns(callInfo =>
            {
                var agent = callInfo.Arg<AgentDefinition>();
                var roomId = callInfo.ArgAt<string>(6);
                lock (_turnLock) { _turnCalls.Add((agent, roomId)); }
                return resultFactory(agent);
            });
    }

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task RoomNotFound_ReturnsImmediately_NoTurnsRun()
    {
        // No room seeded — room lookup returns null
        await _runner.RunRoundsAsync("nonexistent-room");

        Assert.Empty(_turnCalls);
    }

    [Fact]
    public async Task AllAgentsPass_RunsOnlyOneRound()
    {
        await SeedRoomAsync(withActiveTask: true);
        await SeedAgentLocationAsync("engineer-1", "main");
        await SeedAgentLocationAsync("reviewer-1", "main");

        // Default turn runner returns PASS for everyone
        await _runner.RunRoundsAsync("main");

        // Planner + 2 idle agents = 3 turns in round 1
        // All PASS → no round 2
        var plannerCalls = _turnCalls.Count(c => c.Agent.Id == "planner-1");
        Assert.Equal(1, plannerCalls);
        var nonPlannerCalls = _turnCalls.Where(c => c.Agent.Id != "planner-1").ToList();
        Assert.Equal(2, nonPlannerCalls.Count);
        // Total = 3 (exactly 1 round)
        Assert.Equal(3, _turnCalls.Count);
    }

    [Fact]
    public async Task NonPassWithActiveTask_RunsMultipleRounds()
    {
        await SeedRoomAsync(withActiveTask: true);
        await SeedAgentLocationAsync("engineer-1", "main");

        // Planner returns non-pass, tagging engineer
        SetupTurnRunner(agent =>
        {
            if (agent.Role == "Planner")
                return new AgentTurnResult(agent, "We need @Hephaestus to implement this.", IsNonPass: true);
            return new AgentTurnResult(agent, "Done implementing.", IsNonPass: true);
        });

        await _runner.RunRoundsAsync("main");

        // Should run up to 3 rounds (MaxRoundsPerTrigger)
        var plannerCalls = _turnCalls.Count(c => c.Agent.Id == "planner-1");
        Assert.Equal(3, plannerCalls);
    }

    [Fact]
    public async Task NonPassWithoutActiveTask_StopsAfterOneRound()
    {
        await SeedRoomAsync(withActiveTask: false);
        await SeedAgentLocationAsync("engineer-1", "main");

        SetupTurnRunner(agent =>
            new AgentTurnResult(agent, "Some response", IsNonPass: true));

        await _runner.RunRoundsAsync("main");

        // Only 1 round because no active task
        var plannerCalls = _turnCalls.Count(c => c.Agent.Id == "planner-1");
        Assert.Equal(1, plannerCalls);
    }

    [Fact]
    public async Task CancellationBeforeStart_NoTurnsRun()
    {
        await SeedRoomAsync(withActiveTask: true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await _runner.RunRoundsAsync("main", cts.Token);

        Assert.Empty(_turnCalls);
    }

    [Fact]
    public async Task PlannerTagsAgents_RunsOnlyTaggedAgents()
    {
        await SeedRoomAsync();
        await SeedAgentLocationAsync("engineer-1", "main");
        await SeedAgentLocationAsync("reviewer-1", "main");
        await SeedAgentLocationAsync("designer-1", "main");

        SetupTurnRunner(agent =>
        {
            if (agent.Role == "Planner")
                return new AgentTurnResult(agent, "Let's have @Hephaestus implement this.", IsNonPass: true);
            return new AgentTurnResult(agent, "PASS", IsNonPass: false);
        });

        await _runner.RunRoundsAsync("main");

        // Planner tags Hephaestus only → only engineer runs (not reviewer or designer)
        var nonPlannerAgents = _turnCalls
            .Where(c => c.Agent.Id != "planner-1")
            .Select(c => c.Agent.Id)
            .Distinct()
            .ToList();
        Assert.Contains("engineer-1", nonPlannerAgents);
        Assert.DoesNotContain("reviewer-1", nonPlannerAgents);
        Assert.DoesNotContain("designer-1", nonPlannerAgents);
    }

    [Fact]
    public async Task NoTaggedAgents_FallsBackToIdleAgents_MaxThree()
    {
        await SeedRoomAsync();
        // Seed 4 idle agents in the room (planner excluded from fallback)
        await SeedAgentLocationAsync("engineer-1", "main");
        await SeedAgentLocationAsync("reviewer-1", "main");
        await SeedAgentLocationAsync("designer-1", "main");
        await SeedAgentLocationAsync("writer-1", "main");

        SetupTurnRunner(agent =>
        {
            if (agent.Role == "Planner")
                return new AgentTurnResult(agent, "PASS", IsNonPass: false);
            return new AgentTurnResult(agent, "PASS", IsNonPass: false);
        });

        await _runner.RunRoundsAsync("main");

        // Planner passes, no agents tagged → idle fallback, capped at 3
        var nonPlannerCalls = _turnCalls.Where(c => c.Agent.Id != "planner-1").ToList();
        Assert.True(nonPlannerCalls.Count <= 3,
            $"Expected at most 3 fallback agents, got {nonPlannerCalls.Count}");
        Assert.True(nonPlannerCalls.Count > 0, "Expected at least 1 fallback agent");
    }

    [Fact]
    public async Task WorkingAgentsSkipped()
    {
        await SeedRoomAsync();
        await SeedAgentLocationAsync("engineer-1", "main", state: "Working");
        await SeedAgentLocationAsync("reviewer-1", "main", state: "Idle");

        SetupTurnRunner(agent =>
        {
            if (agent.Role == "Planner")
                return new AgentTurnResult(agent, "PASS", IsNonPass: false);
            return new AgentTurnResult(agent, "PASS", IsNonPass: false);
        });

        await _runner.RunRoundsAsync("main");

        // Engineer is Working → skipped. Only reviewer should run.
        var nonPlannerAgents = _turnCalls
            .Where(c => c.Agent.Id != "planner-1")
            .Select(c => c.Agent.Id)
            .ToList();
        Assert.DoesNotContain("engineer-1", nonPlannerAgents);
        Assert.Contains("reviewer-1", nonPlannerAgents);
    }

    [Fact]
    public async Task PlannerNotInCatalog_FallsBackToIdleAgents()
    {
        // Create a runner with no Planner in the catalog
        var catalogNoPlanner = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents: [Engineer, Reviewer]);

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var runnerNoPlanner = new ConversationRoundRunner(
            scopeFactory, catalogNoPlanner, _turnRunner,
            NullLogger<ConversationRoundRunner>.Instance);

        await SeedRoomAsync();
        await SeedAgentLocationAsync("engineer-1", "main");
        await SeedAgentLocationAsync("reviewer-1", "main");

        _turnCalls.Clear();
        await runnerNoPlanner.RunRoundsAsync("main");

        // No planner → falls back to idle agents directly
        Assert.True(_turnCalls.Count > 0);
        Assert.DoesNotContain(_turnCalls, c => c.Agent.Role == "Planner");
    }

    [Fact]
    public async Task CancellationDuringAgentLoop_StopsProcessing()
    {
        await SeedRoomAsync(withActiveTask: true);
        await SeedAgentLocationAsync("engineer-1", "main");
        await SeedAgentLocationAsync("reviewer-1", "main");

        using var cts = new CancellationTokenSource();

        SetupTurnRunner(agent =>
        {
            if (agent.Role == "Planner")
            {
                // Cancel after planner runs, tagging both agents
                cts.Cancel();
                return new AgentTurnResult(agent,
                    "@Hephaestus and @Athena should work on this.", IsNonPass: true);
            }
            return new AgentTurnResult(agent, "Done", IsNonPass: true);
        });

        await _runner.RunRoundsAsync("main", cts.Token);

        // Planner ran, but subsequent agents should be skipped due to cancellation
        var plannerRan = _turnCalls.Any(c => c.Agent.Id == "planner-1");
        Assert.True(plannerRan);
        // At most 1 more agent may have started before cancellation was checked
        Assert.True(_turnCalls.Count <= 2,
            $"Expected at most 2 turns (planner + possibly 1 agent before cancel check), got {_turnCalls.Count}");
    }

    [Fact]
    public async Task MultipleRounds_PlannerRunsEachRound()
    {
        await SeedRoomAsync(withActiveTask: true);
        await SeedAgentLocationAsync("engineer-1", "main");

        SetupTurnRunner(agent =>
            new AgentTurnResult(agent, "Working on it", IsNonPass: true));

        await _runner.RunRoundsAsync("main");

        // 3 rounds × planner each = 3 planner calls
        var plannerCalls = _turnCalls.Count(c => c.Agent.Id == "planner-1");
        Assert.Equal(3, plannerCalls);
    }
}
