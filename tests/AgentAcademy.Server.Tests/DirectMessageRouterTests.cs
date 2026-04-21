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
/// Unit tests for <see cref="DirectMessageRouter"/>: agent lookup,
/// breakout room forwarding, targeted turn execution, and fallback behavior.
/// Uses real DI container with in-memory SQLite and mocked IAgentTurnRunner.
/// </summary>
public sealed class DirectMessageRouterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IAgentTurnRunner _turnRunner;
    private readonly DirectMessageRouter _router;
    private readonly AgentCatalogOptions _catalog;
    private readonly List<(AgentDefinition Agent, string RoomId)> _turnCalls = [];
    private readonly object _turnLock = new();

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

    public DirectMessageRouterTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents: [Engineer, Reviewer]);

        _turnRunner = Substitute.For<IAgentTurnRunner>();

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
                return new AgentTurnResult(agent, "Done", IsNonPass: true);
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
            specsDir: Path.Combine(Path.GetTempPath(), $"dmrouter-test-specs-{Guid.NewGuid()}"),
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
        _router = new DirectMessageRouter(
            scopeFactory, _catalog, _turnRunner,
            NullLogger<DirectMessageRouter>.Instance);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private AgentAcademyDbContext CreateDb()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
    }

    private async Task SeedRoomAsync(string roomId = "main", string name = "Main Room")
    {
        using var db = CreateDb();
        db.Rooms.Add(new RoomEntity
        {
            Id = roomId, Name = name, Status = "Active", Topic = "",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentLocationAsync(string agentId, string roomId,
        string state = "Idle", string? breakoutRoomId = null)
    {
        using var db = CreateDb();
        db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = agentId, RoomId = roomId, State = state,
            BreakoutRoomId = breakoutRoomId, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedDirectMessageAsync(string recipientAgentId,
        string senderId = "human-1", string senderName = "User", string content = "Hey there",
        string roomId = "main")
    {
        using var db = CreateDb();
        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid().ToString(),
            RoomId = roomId,
            SenderId = senderId,
            SenderName = senderName,
            SenderKind = "Human",
            Kind = "DirectMessage",
            Content = content,
            RecipientId = recipientAgentId,
            SentAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedBreakoutRoomAsync(string breakoutId, string parentRoomId, string agentId)
    {
        using var db = CreateDb();
        db.BreakoutRooms.Add(new BreakoutRoomEntity
        {
            Id = breakoutId,
            ParentRoomId = parentRoomId,
            AssignedAgentId = agentId,
            Name = $"Breakout-{agentId}",
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task AgentNotInCatalog_ReturnsEarly_NoTurnsRun()
    {
        await _router.RouteAsync("nonexistent-agent");

        Assert.Empty(_turnCalls);
    }

    [Fact]
    public async Task AgentInRegularRoom_RunsTargetedTurn()
    {
        await SeedRoomAsync();
        await SeedAgentLocationAsync("engineer-1", "main");

        await _router.RouteAsync("engineer-1");

        Assert.Single(_turnCalls);
        Assert.Equal("engineer-1", _turnCalls[0].Agent.Id);
        Assert.Equal("main", _turnCalls[0].RoomId);
    }

    [Fact]
    public async Task AgentInBreakoutRoom_ForwardsDmsToBreakout_NoTurnRun()
    {
        await SeedRoomAsync();
        await SeedBreakoutRoomAsync("breakout-1", "main", "engineer-1");
        await SeedAgentLocationAsync("engineer-1", "main",
            state: "Working", breakoutRoomId: "breakout-1");
        await SeedDirectMessageAsync("engineer-1", content: "Check this bug");

        await _router.RouteAsync("engineer-1");

        // DMs forwarded to breakout room, no targeted turn run
        Assert.Empty(_turnCalls);

        // Verify DMs were acknowledged
        using var db = CreateDb();
        var unackedDms = await db.Messages
            .Where(m => m.RecipientId == "engineer-1" && m.AcknowledgedAt == null)
            .CountAsync();
        Assert.Equal(0, unackedDms);
    }

    [Fact]
    public async Task AgentInBreakoutRoom_NoDms_StillNoTurnRun()
    {
        await SeedRoomAsync();
        await SeedBreakoutRoomAsync("breakout-1", "main", "engineer-1");
        await SeedAgentLocationAsync("engineer-1", "main",
            state: "Working", breakoutRoomId: "breakout-1");

        // No DMs seeded
        await _router.RouteAsync("engineer-1");

        Assert.Empty(_turnCalls);
    }

    [Fact]
    public async Task AgentNoLocation_FallsBackToFirstRoom()
    {
        await SeedRoomAsync("room-a", "Room A");
        // No agent location seeded

        await _router.RouteAsync("engineer-1");

        Assert.Single(_turnCalls);
        Assert.Equal("room-a", _turnCalls[0].RoomId);
    }

    [Fact]
    public async Task RoomNotFound_ReturnsEarly_NoTurnsRun()
    {
        // Agent has location in a room that doesn't exist
        await SeedAgentLocationAsync("engineer-1", "phantom-room");

        await _router.RouteAsync("engineer-1");

        Assert.Empty(_turnCalls);
    }

    [Fact]
    public async Task CaseInsensitiveAgentLookup()
    {
        await SeedRoomAsync();
        await SeedAgentLocationAsync("engineer-1", "main");

        await _router.RouteAsync("ENGINEER-1");

        Assert.Single(_turnCalls);
        Assert.Equal("engineer-1", _turnCalls[0].Agent.Id);
    }

    [Fact]
    public async Task BreakoutForwarding_PostsMessageContent()
    {
        await SeedRoomAsync();
        await SeedBreakoutRoomAsync("breakout-1", "main", "engineer-1");
        await SeedAgentLocationAsync("engineer-1", "main",
            state: "Working", breakoutRoomId: "breakout-1");
        await SeedDirectMessageAsync("engineer-1", content: "Please review PR #42");

        await _router.RouteAsync("engineer-1");

        // Verify a message was posted to the breakout room with the DM content
        using var db = CreateDb();
        var breakoutMessages = await db.BreakoutMessages
            .Where(m => m.BreakoutRoomId == "breakout-1")
            .ToListAsync();
        Assert.Single(breakoutMessages);
        Assert.Contains("Please review PR #42", breakoutMessages[0].Content);
    }
}
