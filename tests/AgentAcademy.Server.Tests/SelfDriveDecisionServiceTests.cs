using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// P1.2: Tests for the 12-branch decision tree in
/// <see cref="SelfDriveDecisionService"/>. Each branch is exercised
/// through <see cref="SelfDriveDecisionService.DecideAsync"/> with a
/// real in-memory SQLite + scoped sprint/room services and a
/// mocked <see cref="IAgentOrchestrator"/>.
///
/// MinIntervalBetweenContinuationsMs is set to 0 so the schedule path
/// runs synchronously without Task.Delay — eliminating fire-and-forget
/// timing flake.
/// </summary>
public sealed class SelfDriveDecisionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ICostGuard _costGuard;
    private readonly SelfDriveOptions _options;
    private readonly SelfDriveDecisionService _sut;
    private readonly IRoomService _roomService;

    private readonly ListLogger _logSink = new();

    public SelfDriveDecisionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opts => opts.UseSqlite(_connection));
        services.AddSingleton<ISprintService>(sp =>
        {
            // Use a single ISprintService backed by the connection so all
            // scopes see the same DB state. NSubstitute generates a no-op
            // for unimplemented members; we only stub the two we call.
            var stub = Substitute.For<ISprintService>();
            stub.GetSprintByIdAsync(Arg.Any<string>())
                .Returns(callInfo =>
                {
                    var id = callInfo.Arg<string>();
                    using var localScope = sp.CreateScope();
                    var db = localScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
                    return db.Sprints.AsNoTracking().FirstOrDefault(s => s.Id == id);
                });
            stub.MarkSprintBlockedAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(callInfo =>
                {
                    var id = callInfo.ArgAt<string>(0);
                    var reason = callInfo.ArgAt<string>(1);
                    using var localScope = sp.CreateScope();
                    var db = localScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
                    var sprint = db.Sprints.First(s => s.Id == id);
                    sprint.BlockedAt = DateTime.UtcNow;
                    sprint.BlockReason = reason;
                    db.SaveChanges();
                    return sprint;
                });
            return stub;
        });
        _roomService = Substitute.For<IRoomService>();
        services.AddScoped<IRoomService>(_ => _roomService);

        _serviceProvider = services.BuildServiceProvider();

        // Initialize schema
        using (var scope = _serviceProvider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>()
                .Database.EnsureCreated();
        }

        _orchestrator = Substitute.For<IAgentOrchestrator>();
        _orchestrator.TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

        _costGuard = Substitute.For<ICostGuard>();
        _costGuard.ShouldHaltAsync(Arg.Any<SprintEntity>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _options = new SelfDriveOptions
        {
            Enabled = true,
            MaxRoundsPerSprint = 50,
            MaxRoundsPerStage = 20,
            MaxConsecutiveSelfDriveContinuations = 8,
            MinIntervalBetweenContinuationsMs = 0,
        };

        _sut = new SelfDriveDecisionService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _orchestrator,
            _costGuard,
            Options.Create(_options),
            _logSink);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ────────────────────────────────────────────────

    private async Task<SprintEntity> SeedSprintAsync(
        string id = "sprint-1",
        string status = "Active",
        string stage = "Implementation",
        bool awaitingSignOff = false,
        DateTime? blockedAt = null,
        int roundsThisSprint = 1,
        int roundsThisStage = 1,
        int selfDriveContinuations = 0,
        int? maxRoundsOverride = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        // Seed a Room entity so ActivityEventEntity FK on RoomId resolves.
        if (!db.Rooms.Any(r => r.Id == "room-A"))
        {
            db.Rooms.Add(new RoomEntity
            {
                Id = "room-A",
                Name = "room-A",
                Status = "Active",
                CurrentPhase = "Discussion",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        var sprint = new SprintEntity
        {
            Id = id,
            Number = 1,
            WorkspacePath = $"/tmp/{id}",
            Status = status,
            CurrentStage = stage,
            AwaitingSignOff = awaitingSignOff,
            BlockedAt = blockedAt,
            BlockReason = blockedAt is null ? null : "test",
            RoundsThisSprint = roundsThisSprint,
            RoundsThisStage = roundsThisStage,
            SelfDriveContinuations = selfDriveContinuations,
            LastRoundCompletedAt = DateTime.UtcNow,
            MaxRoundsOverride = maxRoundsOverride,
            CreatedAt = DateTime.UtcNow,
        };
        db.Sprints.Add(sprint);
        await db.SaveChangesAsync();
        return sprint;
    }

    private void SetRoom(string roomId, RoomStatus status, TaskSnapshot? activeTask = null)
    {
        var snap = new RoomSnapshot(
            Id: roomId,
            Name: roomId,
            Topic: null,
            Status: status,
            CurrentPhase: CollaborationPhase.Discussion,
            ActiveTask: activeTask,
            Participants: new(),
            RecentMessages: new(),
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);
        _roomService.GetRoomAsync(roomId).Returns(snap);
    }

    private static TaskSnapshot SampleTask() => new(
        Id: "task-1",
        Title: "x",
        Description: "y",
        SuccessCriteria: "criteria",
        Status: AgentAcademy.Shared.Models.TaskStatus.Active,
        Type: TaskType.Feature,
        CurrentPhase: CollaborationPhase.Discussion,
        CurrentPlan: "",
        ValidationStatus: WorkstreamStatus.NotStarted,
        ValidationSummary: "",
        ImplementationStatus: WorkstreamStatus.InProgress,
        ImplementationSummary: "",
        PreferredRoles: new(),
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        AssignedAgentId: "engineer-1");

    private static RoundRunOutcome NonPass(int rounds = 1) => new(true, rounds);
    private static RoundRunOutcome AllPass(int rounds = 1) => new(false, rounds);

    // ── Decision tree branches ─────────────────────────────────

    [Fact]
    public async Task Step0_FeatureDisabled_NoEnqueue()
    {
        await SeedSprintAsync();
        SetRoom("room-A", RoomStatus.Active, SampleTask());
        _options.Enabled = false;

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Step1_NoCapturedSprint_NoEnqueue()
    {
        await _sut.DecideAsync("room-A", null, NonPass(), CancellationToken.None);
        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Step2_SprintBlocked_NoEnqueue()
    {
        await SeedSprintAsync(blockedAt: DateTime.UtcNow);
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Step3_SprintAwaitingSignOff_NoEnqueue()
    {
        await SeedSprintAsync(awaitingSignOff: true);
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Step4_SprintNotActive_NoEnqueue()
    {
        await SeedSprintAsync(status: "Completed");
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Step5_RoomMissing_NoEnqueue()
    {
        await SeedSprintAsync();
        // Don't seed room — IRoomService.GetRoomAsync returns null by default for substitute.

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Step5_RoomCompleted_NoEnqueue()
    {
        await SeedSprintAsync();
        SetRoom("room-A", RoomStatus.Completed, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Step6_RoundCapHit_BlocksSprint()
    {
        await SeedSprintAsync(roundsThisSprint: 50);
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
        Assert.NotNull(await GetSprint("sprint-1"));
        var sprint = await GetSprint("sprint-1");
        Assert.NotNull(sprint!.BlockedAt);
        Assert.Contains("Round cap reached", sprint.BlockReason);
    }

    [Fact]
    public async Task Step6_MaxRoundsOverride_AppliesPerSprint()
    {
        // Override = 3, current rounds = 3 → cap hit
        await SeedSprintAsync(roundsThisSprint: 3, maxRoundsOverride: 3);
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        var sprint = await GetSprint("sprint-1");
        Assert.NotNull(sprint!.BlockedAt);
        Assert.Contains("3/3", sprint.BlockReason);
    }

    [Fact]
    public async Task Step7_StageRoundCapHit_BlocksSprint()
    {
        await SeedSprintAsync(roundsThisStage: 20);
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        var sprint = await GetSprint("sprint-1");
        Assert.NotNull(sprint!.BlockedAt);
        Assert.Contains("Stage round cap", sprint.BlockReason);
    }

    [Fact]
    public async Task Step8_ContinuationCapHit_BlocksSprint()
    {
        await SeedSprintAsync(selfDriveContinuations: 8);
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        var sprint = await GetSprint("sprint-1");
        Assert.NotNull(sprint!.BlockedAt);
        Assert.Contains("Continuation cap reached", sprint.BlockReason);
    }

    [Fact]
    public async Task Step9_AllPassOutcome_NoEnqueue()
    {
        await SeedSprintAsync();
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", AllPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
        // And sprint stays active.
        var sprint = await GetSprint("sprint-1");
        Assert.Null(sprint!.BlockedAt);
    }

    [Fact]
    public async Task Step10_IntakeStage_NoActiveTask_NoEnqueue()
    {
        await SeedSprintAsync(stage: "Intake");
        SetRoom("room-A", RoomStatus.Active, activeTask: null);

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Step10_IntakeStage_WithActiveTask_Enqueues()
    {
        await SeedSprintAsync(stage: "Intake");
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        // Wait briefly for fire-and-forget Task.Run scheduler to enqueue.
        await WaitForEnqueueAsync();
        _orchestrator.Received().TryEnqueueSystemContinuation("room-A", "sprint-1");
    }

    [Fact]
    public async Task Step10_DiscussionStage_NoActiveTask_NoEnqueue()
    {
        await SeedSprintAsync(stage: "Discussion");
        SetRoom("room-A", RoomStatus.Active, activeTask: null);

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Step10_ImplementationStage_NoActiveTask_StillEnqueues()
    {
        // Step 10 only gates Intake and Discussion. Implementation
        // self-drives even without an active task entity (work product
        // is already in flight via the implementation harness).
        await SeedSprintAsync(stage: "Implementation");
        SetRoom("room-A", RoomStatus.Active, activeTask: null);

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        await WaitForEnqueueAsync();
        _orchestrator.Received().TryEnqueueSystemContinuation("room-A", "sprint-1");
    }

    [Fact]
    public async Task Step12_CostGuardHalts_BlocksSprint()
    {
        await SeedSprintAsync();
        SetRoom("room-A", RoomStatus.Active, SampleTask());
        _costGuard.ShouldHaltAsync(Arg.Any<SprintEntity>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        _orchestrator.DidNotReceive().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>());
        var sprint = await GetSprint("sprint-1");
        Assert.NotNull(sprint!.BlockedAt);
        Assert.Contains("Cost cap reached", sprint.BlockReason);
    }

    [Fact]
    public async Task HappyPath_AllGatesPass_EnqueuesAndEmitsActivityEvent()
    {
        await SeedSprintAsync(stage: "Implementation");
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        await WaitForEnqueueAsync();
        _orchestrator.Received().TryEnqueueSystemContinuation("room-A", "sprint-1");

        // ActivityEvent persisted with the right type. Poll briefly because
        // EmitContinuationActivityEventAsync runs after the orchestrator call
        // returns inside the same fire-and-forget scheduler task.
        ActivityEventEntity? evt = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            using var pollScope = _serviceProvider.CreateScope();
            var pollDb = pollScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            evt = await pollDb.ActivityEvents
                .FirstOrDefaultAsync(e => e.Type == nameof(ActivityEventType.SprintRoundContinuationScheduled));
            if (evt != null) break;
            await Task.Delay(25);
        }
        Assert.NotNull(evt);
        Assert.Equal("room-A", evt.RoomId);
        Assert.Equal("Info", evt.Severity);
        Assert.NotNull(evt.MetadataJson);
        Assert.Contains("\"sprintId\":\"sprint-1\"", evt.MetadataJson);
    }

    [Fact]
    public async Task DropDuringDelay_OrchestratorReturnsFalse_NoActivityEvent()
    {
        // Simulate dedupe drop: orchestrator says false (HumanMessage
        // already queued for the room). We should NOT emit the
        // ActivityEvent in that case.
        _orchestrator.TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);
        await SeedSprintAsync(stage: "Implementation");
        SetRoom("room-A", RoomStatus.Active, SampleTask());

        await _sut.DecideAsync("room-A", "sprint-1", NonPass(), CancellationToken.None);

        await WaitForEnqueueAsync();
        _orchestrator.Received().TryEnqueueSystemContinuation("room-A", "sprint-1");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var evt = await db.ActivityEvents
            .FirstOrDefaultAsync(e => e.Type == nameof(ActivityEventType.SprintRoundContinuationScheduled));
        Assert.Null(evt);
    }

    // ── Async waiter ────────────────────────────────────────────

    private async Task WaitForEnqueueAsync(int maxMs = 2000)
    {
        // Fire-and-forget Task.Run inside DecideAsync. Poll briefly.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            try { _orchestrator.Received().TryEnqueueSystemContinuation(Arg.Any<string>(), Arg.Any<string>()); return; }
            catch { /* not yet */ }
            await Task.Delay(25);
        }
    }

    private async Task<SprintEntity?> GetSprint(string id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        return await db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
    }

    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger<SelfDriveDecisionService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (level >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                var msg = formatter(state, ex);
                throw new Xunit.Sdk.XunitException($"[{level}] {msg}{(ex != null ? "\n" + ex : "")}");
            }
        }
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<SelfDriveDecisionService>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add($"[{level}] {formatter(state, ex)}{(ex != null ? "\n" + ex : "")}");
            }
        }
    }
}
