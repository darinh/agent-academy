using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Notifications.Contracts;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="TeamIdleNotificationService"/> — verifies the
/// "team is idle, awaiting instructions" notification fires once when the
/// last active sprint wraps up, debounces correctly, and resets when a
/// new sprint starts. Closes P1.7 / G7.
/// </summary>
public class TeamIdleNotificationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly ActivityBroadcaster _broadcaster;
    private readonly INotificationManager _notificationManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceProvider _sp;
    private readonly TeamIdleNotificationService _sut;

    public TeamIdleNotificationServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(o => o.UseSqlite(_connection));
        _sp = services.BuildServiceProvider();

        using (var scope = _sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            ctx.Database.EnsureCreated();
        }

        _db = _sp.GetRequiredService<AgentAcademyDbContext>();
        _scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        _broadcaster = new ActivityBroadcaster();
        _notificationManager = Substitute.For<INotificationManager>();
        _notificationManager.SendToAllAsync(Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>())
            .Returns(1);

        _sut = new TeamIdleNotificationService(
            _broadcaster,
            _notificationManager,
            _scopeFactory,
            NullLogger<TeamIdleNotificationService>.Instance);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _sp.Dispose();
        _connection.Dispose();
    }

    private static SprintEntity NewSprint(string status = "Active", int number = 1) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Number = number,
        WorkspacePath = "/tmp/ws",
        CurrentStage = "Intake",
        Status = status,
        CreatedAt = DateTime.UtcNow,
    };

    private static ActivityEvent SprintEvent(ActivityEventType type) => new(
        Id: Guid.NewGuid().ToString("N"),
        Type: type,
        Severity: ActivitySeverity.Info,
        RoomId: "room-1",
        ActorId: null,
        TaskId: null,
        Message: $"sprint event {type}",
        CorrelationId: null,
        OccurredAt: DateTime.UtcNow,
        Metadata: null);

    private async Task FlushAsync()
    {
        // The subscriber dispatches an async DB query via fire-and-forget.
        // Spin a few times until pending work drains.
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(20);
            if (_notificationManager.ReceivedCalls().Any())
                return;
        }
    }

    [Fact]
    public async Task SprintCompleted_WithNoRemainingActiveSprints_FiresIdleNotification()
    {
        await _sut.StartAsync(CancellationToken.None);
        // Only sprint exists and is now Completed (the SprintService writes the
        // status row before broadcasting the SprintCompleted activity event).
        _db.Sprints.Add(NewSprint(status: "Completed"));
        await _db.SaveChangesAsync();

        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintCompleted));
        await FlushAsync();

        await _notificationManager.Received(1).SendToAllAsync(
            Arg.Is<NotificationMessage>(m =>
                m.Type == NotificationType.NeedsInput
                && m.Title == "Team is idle"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SprintCompleted_WithRemainingActiveSprints_DoesNotFireIdleNotification()
    {
        await _sut.StartAsync(CancellationToken.None);
        _db.Sprints.Add(NewSprint(status: "Active", number: 2));
        _db.Sprints.Add(NewSprint(status: "Completed", number: 1));
        await _db.SaveChangesAsync();

        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintCompleted));
        await FlushAsync();

        await _notificationManager.DidNotReceiveWithAnyArgs()
            .SendToAllAsync(default!, default);
    }

    [Fact]
    public async Task SprintCompleted_FiringTwiceWhileIdle_DebouncesToSingleNotification()
    {
        await _sut.StartAsync(CancellationToken.None);
        _db.Sprints.Add(NewSprint(status: "Completed"));
        await _db.SaveChangesAsync();

        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintCompleted));
        await FlushAsync();
        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintCompleted));
        await Task.Delay(60);

        await _notificationManager.Received(1).SendToAllAsync(
            Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SprintStarted_AfterIdle_ResetsLatch_AndNextCompletionNotifiesAgain()
    {
        await _sut.StartAsync(CancellationToken.None);

        // First completion → idle notification fires.
        _db.Sprints.Add(NewSprint(status: "Completed", number: 1));
        await _db.SaveChangesAsync();
        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintCompleted));
        await FlushAsync();

        // New sprint starts — latch resets.
        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintStarted));
        await Task.Delay(20);

        // Second sprint also completes → second idle notification.
        _notificationManager.ClearReceivedCalls();
        _db.Sprints.Add(NewSprint(status: "Completed", number: 2));
        await _db.SaveChangesAsync();
        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintCompleted));
        await FlushAsync();

        await _notificationManager.Received(1).SendToAllAsync(
            Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SprintStarted_BumpsGenerationCounter_AndClearsLatch()
    {
        // Focused unit test for the generation-guard primitive that closes the
        // SprintCompleted→SprintStarted race. Verifies via internal accessors
        // that (a) every SprintStarted increments the generation counter and
        // (b) the idle latch is cleared. The composite behavior (idle check
        // aborting when generation advances mid-flight) is covered by code
        // inspection — deterministic timing-based testing of the in-flight
        // window would require a production seam we deliberately avoid.
        await _sut.StartAsync(CancellationToken.None);

        var startGen = _sut.SprintStartGenerationForTests;

        // Force the latch on, then broadcast SprintStarted.
        _db.Sprints.Add(NewSprint(status: "Completed"));
        await _db.SaveChangesAsync();
        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintCompleted));
        await FlushAsync();
        Assert.True(_sut.IdleNotifiedForTests, "Idle latch should be set after first idle notification.");

        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintStarted));
        await Task.Delay(20);

        Assert.True(_sut.SprintStartGenerationForTests > startGen,
            "SprintStarted must increment the generation counter.");
        Assert.False(_sut.IdleNotifiedForTests,
            "SprintStarted must clear the idle latch so the next idle period notifies.");

        // SprintStarted with no prior latch set: must STILL bump the counter
        // (this is what closes the in-flight race — the increment cannot be
        // gated on _idleNotified being true).
        var beforeSecond = _sut.SprintStartGenerationForTests;
        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintStarted));
        await Task.Delay(20);
        Assert.True(_sut.SprintStartGenerationForTests > beforeSecond,
            "SprintStarted must always bump generation, even when latch is already clear.");
    }

    [Fact]
    public async Task SprintCompleted_WithActiveSprintAndPriorSprintStarted_DoesNotNotify()
    {
        // Exercises the activeCount > 0 short-circuit (a different guard than
        // the generation race, but worth pinning down): a sprint completion
        // posted right after another sprint started (and is still Active) must
        // never produce an idle notification.
        await _sut.StartAsync(CancellationToken.None);
        _db.Sprints.Add(NewSprint(status: "Active", number: 2));
        await _db.SaveChangesAsync();
        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintStarted));
        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintCompleted));
        await Task.Delay(60);

        await _notificationManager.DidNotReceiveWithAnyArgs()
            .SendToAllAsync(default!, default);
    }

    [Fact]
    public async Task SprintCancelled_WithNoRemainingActiveSprints_AlsoFiresIdleNotification()
    {
        await _sut.StartAsync(CancellationToken.None);
        _db.Sprints.Add(NewSprint(status: "Cancelled"));
        await _db.SaveChangesAsync();

        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintCancelled));
        await FlushAsync();

        await _notificationManager.Received(1).SendToAllAsync(
            Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnrelatedActivityEvents_DoNotTriggerIdleCheck()
    {
        await _sut.StartAsync(CancellationToken.None);
        _db.Sprints.Add(NewSprint(status: "Completed"));
        await _db.SaveChangesAsync();

        _broadcaster.Broadcast(SprintEvent(ActivityEventType.MessagePosted));
        _broadcaster.Broadcast(SprintEvent(ActivityEventType.AgentThinking));
        await Task.Delay(60);

        await _notificationManager.DidNotReceiveWithAnyArgs()
            .SendToAllAsync(default!, default);
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromBroadcaster()
    {
        await _sut.StartAsync(CancellationToken.None);
        await _sut.StopAsync(CancellationToken.None);
        _db.Sprints.Add(NewSprint(status: "Completed"));
        await _db.SaveChangesAsync();

        _broadcaster.Broadcast(SprintEvent(ActivityEventType.SprintCompleted));
        await Task.Delay(60);

        await _notificationManager.DidNotReceiveWithAnyArgs()
            .SendToAllAsync(default!, default);
    }
}
