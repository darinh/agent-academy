using AgentAcademy.Server.Data;
using AgentAcademy.Server.Notifications.Contracts;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Hosted service that emits a "Team is idle, awaiting instructions" notification
/// after the last active sprint wraps up. Closes Phase 1 / G7 — without this,
/// the human has no out-of-band signal that the agents have run out of work and
/// need fresh direction.
///
/// <para>
/// Trigger: <see cref="ActivityEventType.SprintCompleted"/> or
/// <see cref="ActivityEventType.SprintCancelled"/>. After either, the service
/// queries the DB for sprints with <c>Status == "Active"</c>. If zero, it
/// dispatches a single idle notification and latches a debounce flag so it
/// does not re-fire on subsequent completions in an already-idle state.
/// </para>
/// <para>
/// The latch resets when <see cref="ActivityEventType.SprintStarted"/> arrives
/// (a new sprint means the team is no longer idle), so the next idle period
/// produces another notification.
/// </para>
/// <para>
/// <b>Scope</b>: This iteration handles "all sprints have wrapped up" idleness.
/// Sprints that are <c>AwaitingSignOff</c> (still <c>Active</c> but paused for
/// human approval) already emit their own sign-off notifications, so they are
/// intentionally treated as "still active" here — adding them would double-notify
/// the human about the same paused-for-input state. The "blocked" notification
/// from the P1.7 spec is deferred until the self-evaluation work in P1.4 lands
/// (no Blocked status exists yet).
/// </para>
/// </summary>
public sealed class TeamIdleNotificationService : IHostedService, IDisposable
{
    private readonly IActivityBroadcaster _broadcaster;
    private readonly INotificationManager _notificationManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TeamIdleNotificationService> _logger;
    private Action? _unsubscribe;

    // Debounce: once we've notified the human that the team is idle, don't
    // notify again until a new sprint starts (which indicates the team is busy
    // again). The flag is read+written from the activity-bus subscriber, which
    // ActivityBroadcaster invokes outside its lock — concurrent sprint-completion
    // events on different workspaces could race here, so guard with a lock.
    //
    // The generation counter handles a subtler race: a SprintCompleted handler
    // fires the active-sprint DB query (sees 0), then TryAutoStartNextSprintAsync
    // broadcasts SprintStarted, then our handler resumes and would otherwise
    // notify "team is idle" even though a new sprint just started. We capture
    // the generation before the DB query AND re-check it both before setting
    // the latch and immediately before dispatch — if it advanced at any point,
    // the in-flight idle notification is suppressed.
    private readonly object _stateLock = new();
    private bool _idleNotified;
    private long _sprintStartGeneration;

    // Test-only accessors. Marked internal so the assembly's InternalsVisibleTo
    // grant to the test project allows assertions about debounce state without
    // exposing public surface.
    internal bool IdleNotifiedForTests
    {
        get { lock (_stateLock) { return _idleNotified; } }
    }

    internal long SprintStartGenerationForTests
    {
        get { lock (_stateLock) { return _sprintStartGeneration; } }
    }

    public TeamIdleNotificationService(
        IActivityBroadcaster broadcaster,
        INotificationManager notificationManager,
        IServiceScopeFactory scopeFactory,
        ILogger<TeamIdleNotificationService> logger)
    {
        _broadcaster = broadcaster;
        _notificationManager = notificationManager;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _unsubscribe = _broadcaster.Subscribe(OnActivityEvent);
        _logger.LogInformation(
            "TeamIdleNotificationService started — watching for sprint completions to detect team idleness.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _unsubscribe?.Invoke();
        _unsubscribe = null;
        _logger.LogInformation("TeamIdleNotificationService stopped.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _unsubscribe?.Invoke();
        _unsubscribe = null;
    }

    private void OnActivityEvent(ActivityEvent evt)
    {
        switch (evt.Type)
        {
            case ActivityEventType.SprintStarted:
                // A new sprint means the team has work — clear the latch AND
                // bump the generation counter so any in-flight idle check
                // (which captured the older generation) aborts before sending.
                lock (_stateLock)
                {
                    _sprintStartGeneration++;
                    if (_idleNotified)
                    {
                        _idleNotified = false;
                        _logger.LogDebug(
                            "Idle latch reset — sprint started, team is no longer idle.");
                    }
                }
                break;

            case ActivityEventType.SprintCompleted:
            case ActivityEventType.SprintCancelled:
                // Fire-and-forget — broadcaster invokes subscribers synchronously
                // and we don't want to block other subscribers on a DB query.
                _ = MaybeNotifyIdleAsync(evt);
                break;
        }
    }

    private async Task MaybeNotifyIdleAsync(ActivityEvent triggeringEvent)
    {
        try
        {
            // Capture the generation BEFORE the DB query so we can detect a
            // SprintStarted event that arrives between the count and the send.
            long startGeneration;
            lock (_stateLock)
            {
                startGeneration = _sprintStartGeneration;
            }

            int activeCount;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
                activeCount = await db.Sprints
                    .CountAsync(s => s.Status == "Active");
            }

            if (activeCount > 0)
            {
                _logger.LogDebug(
                    "Sprint {EventType} but {ActiveCount} sprint(s) still active — not idle yet.",
                    triggeringEvent.Type, activeCount);
                return;
            }

            // No active sprints. Atomically check the latch AND verify no
            // SprintStarted slipped in while we were querying. If either
            // gate trips, abort without sending or latching.
            bool shouldSend;
            lock (_stateLock)
            {
                if (_sprintStartGeneration != startGeneration)
                {
                    _logger.LogDebug(
                        "Idle check superseded by SprintStarted (gen {Old} → {New}); skipping notification.",
                        startGeneration, _sprintStartGeneration);
                    return;
                }

                if (_idleNotified)
                {
                    _logger.LogDebug(
                        "Team idle but already notified — debouncing.");
                    return;
                }

                _idleNotified = true;
                shouldSend = true;
            }

            if (!shouldSend)
                return;

            // Final pre-dispatch generation check. Closes the latch→send window
            // a SprintStarted event might exploit between releasing the lock
            // above and actually invoking the provider. If we lose this race
            // we roll back the latch so the next idle period still notifies.
            lock (_stateLock)
            {
                if (_sprintStartGeneration != startGeneration)
                {
                    _idleNotified = false;
                    _logger.LogDebug(
                        "Idle dispatch superseded by SprintStarted just before send (gen {Old} → {New}); aborting and clearing latch.",
                        startGeneration, _sprintStartGeneration);
                    return;
                }
            }

            var message = new NotificationMessage(
                Type: NotificationType.NeedsInput,
                Title: "Team is idle",
                Body: "All sprints have wrapped up. The team is awaiting instructions — start a new sprint or post a goal in the main room.",
                RoomId: triggeringEvent.RoomId,
                AgentName: null);

            var delivered = await _notificationManager.SendToAllAsync(message);
            _logger.LogInformation(
                "Team-idle notification dispatched (triggered by {EventType}); delivered to {Count} provider(s).",
                triggeringEvent.Type, delivered);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to evaluate team-idle state after {EventType}.",
                triggeringEvent.Type);
        }
    }
}
