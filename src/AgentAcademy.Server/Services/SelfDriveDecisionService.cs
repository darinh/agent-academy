using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AgentAcademy.Server.Services;

/// <summary>
/// P1.2 Self-Drive decision service. Implements the 12-branch decision tree
/// from <c>specs/100-product-vision/p1-2-self-drive-design.md</c> §4.6.
///
/// Singleton (the round runner is singleton). Scoped dependencies
/// (<see cref="ISprintService"/>, <see cref="IRoomService"/>,
/// <see cref="AgentAcademyDbContext"/>) are resolved per-call via
/// <see cref="IServiceScopeFactory"/>.
/// </summary>
public sealed class SelfDriveDecisionService : ISelfDriveDecisionService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ICostGuard _costGuard;
    private readonly SelfDriveOptions _options;
    private readonly ILogger<SelfDriveDecisionService> _logger;
    private readonly TimeProvider _timeProvider;
    // Owns its own shutdown CTS so background Task.Delay loops are
    // cancelled on disposal (test teardown, host shutdown). Without
    // this, fire-and-forget scheduler tasks accumulate across test
    // runs and deadlock the suite.
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    public SelfDriveDecisionService(
        IServiceScopeFactory scopeFactory,
        IAgentOrchestrator orchestrator,
        ICostGuard costGuard,
        IOptions<SelfDriveOptions> options,
        ILogger<SelfDriveDecisionService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _orchestrator = orchestrator;
        _costGuard = costGuard;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task DecideAsync(
        string roomId,
        string? capturedSprintId,
        RoundRunOutcome outcome,
        CancellationToken ct)
    {
        try
        {
            await DecideInnerAsync(roomId, capturedSprintId, outcome, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Trigger CT cancelled — caller already exited; ignore.
        }
        catch (Exception ex)
        {
            // Fail open: a decision-service crash MUST NOT propagate to the
            // round runner. The round just completed successfully; the worst
            // case is "no continuation this time", which is recoverable on
            // the next human trigger.
            _logger.LogWarning(ex,
                "Self-drive decision failed for room {RoomId} sprint {SprintId}; idling",
                roomId, capturedSprintId);
        }
    }

    private async Task DecideInnerAsync(
        string roomId,
        string? capturedSprintId,
        RoundRunOutcome outcome,
        CancellationToken ct)
    {
        // Step 0: master kill switch.
        if (!_options.Enabled) return;

        // Step 1: no sprint was active when the round started → not a
        // sprint-driven trigger. Self-drive does not apply.
        if (capturedSprintId is null) return;

        using var scope = _scopeFactory.CreateScope();
        var sprintService = scope.ServiceProvider.GetRequiredService<ISprintService>();
        var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();

        var sprint = await sprintService.GetSprintByIdAsync(capturedSprintId);
        if (sprint is null) return;

        // Steps 2–4: sprint must be Active, not blocked, not awaiting sign-off.
        if (sprint.BlockedAt is not null) return;            // step 2
        if (sprint.AwaitingSignOff) return;                  // step 3
        if (!string.Equals(sprint.Status, "Active", StringComparison.Ordinal)) return; // step 4

        // Step 5: room must exist and not be terminal.
        var room = await roomService.GetRoomAsync(roomId);
        if (room is null) return;
        if (room.Status is RoomStatus.Completed or RoomStatus.Archived) return;

        // Steps 6–8: cap gates. A tripped cap HALTs by marking the sprint
        // blocked — a human checkpoint is required before more work
        // happens. Counter values were just persisted by IncrementRound
        // CountersAsync; we re-read them here via the fresh sprint load.
        var maxRoundsPerSprint = sprint.MaxRoundsOverride ?? _options.MaxRoundsPerSprint;
        if (sprint.RoundsThisSprint >= maxRoundsPerSprint)
        {
            await sprintService.MarkSprintBlockedAsync(
                sprint.Id,
                $"Round cap reached: {sprint.RoundsThisSprint}/{maxRoundsPerSprint}");
            return;
        }
        if (sprint.RoundsThisStage >= _options.MaxRoundsPerStage)
        {
            await sprintService.MarkSprintBlockedAsync(
                sprint.Id,
                $"Stage round cap reached for {sprint.CurrentStage}: " +
                $"{sprint.RoundsThisStage}/{_options.MaxRoundsPerStage}");
            return;
        }
        if (sprint.SelfDriveContinuations >= _options.MaxConsecutiveSelfDriveContinuations)
        {
            await sprintService.MarkSprintBlockedAsync(
                sprint.Id,
                $"Continuation cap reached without human checkpoint: " +
                $"{sprint.SelfDriveContinuations}/{_options.MaxConsecutiveSelfDriveContinuations}");
            return;
        }

        // Step 9: outcome gate. If the round produced only PASS responses,
        // there's nothing to continue — agents already converged.
        if (!outcome.HadNonPassResponse) return;

        // Step 10: stage-aware gate. Intake and Discussion are
        // human-collaboration stages — they idle for human input unless an
        // active task has been created (real work to drive forward).
        if (sprint.CurrentStage is "Intake" or "Discussion")
        {
            if (room.ActiveTask is null) return;
        }

        // Step 12: cost guard (run before scheduling so we never enqueue
        // a continuation that would immediately be capped).
        if (await _costGuard.ShouldHaltAsync(sprint, ct))
        {
            await sprintService.MarkSprintBlockedAsync(sprint.Id, "Cost cap reached");
            return;
        }

        // Step 11: schedule the SystemContinuation enqueue. Min-interval
        // gating must be a *delayed enqueue with re-check*, not an
        // immediate IDLE — IncrementRoundCountersAsync just wrote
        // LastRoundCompletedAt = now, so a "now - last < min-interval ?
        // IDLE" check would always trip and the system would never
        // self-drive. The delay path re-runs the gates after waking so
        // a state change during the wait correctly suppresses the enqueue.
        var lastCompleted = sprint.LastRoundCompletedAt ?? DateTime.UtcNow;
        var elapsedMs = (_timeProvider.GetUtcNow().UtcDateTime - lastCompleted).TotalMilliseconds;
        var remainingMs = _options.MinIntervalBetweenContinuationsMs - elapsedMs;
        var delay = remainingMs > 0 ? TimeSpan.FromMilliseconds(remainingMs) : TimeSpan.Zero;

        // Fire-and-forget. Cancel via the service-owned shutdown CTS so
        // host shutdown / test teardown actually stops the loop. The
        // trigger CT is about to be disposed by its owner (the round
        // runner) and is not appropriate for a long-lived background
        // task.
        _ = Task.Run(() => ScheduleEnqueueAsync(roomId, sprint.Id, delay, _shutdownCts.Token));
    }

    private async Task ScheduleEnqueueAsync(
        string roomId,
        string sprintId,
        TimeSpan delay,
        CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            using var scope = _scopeFactory.CreateScope();
            var sprintService = scope.ServiceProvider.GetRequiredService<ISprintService>();
            var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();

            // Re-check all gates after the delay. State may have changed:
            // sprint blocked by another path, room archived, human posted
            // a HumanMessage that obviated the continuation.
            var sprint = await sprintService.GetSprintByIdAsync(sprintId);
            if (sprint is null) return;
            if (sprint.BlockedAt is not null) return;
            if (sprint.AwaitingSignOff) return;
            if (!string.Equals(sprint.Status, "Active", StringComparison.Ordinal)) return;

            var room = await roomService.GetRoomAsync(roomId);
            if (room is null) return;
            if (room.Status is RoomStatus.Completed or RoomStatus.Archived) return;

            var maxRoundsPerSprint = sprint.MaxRoundsOverride ?? _options.MaxRoundsPerSprint;
            if (sprint.RoundsThisSprint >= maxRoundsPerSprint) return;
            if (sprint.RoundsThisStage >= _options.MaxRoundsPerStage) return;
            if (sprint.SelfDriveContinuations >= _options.MaxConsecutiveSelfDriveContinuations) return;

            // Enqueue. May be dropped by orchestrator dedupe (e.g., a
            // HumanMessage arrived during the delay and is already
            // queued). If so, the post-round decision after that human
            // turn will re-evaluate.
            if (!_orchestrator.TryEnqueueSystemContinuation(roomId, sprintId))
            {
                _logger.LogDebug(
                    "Self-drive continuation for room {RoomId} (sprint {SprintId}) dropped by dedupe",
                    roomId, sprintId);
                return;
            }

            await EmitContinuationActivityEventAsync(scope, sprint, roomId);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Self-drive enqueue scheduling failed for room {RoomId} sprint {SprintId}",
                roomId, sprintId);
        }
    }

    private static async Task EmitContinuationActivityEventAsync(
        IServiceScope scope,
        SprintEntity sprint,
        string roomId)
    {
        // IActivityPublisher.Publish doesn't accept metadata, so write
        // the ActivityEventEntity directly (matches SprintService's
        // QueueEvent helper). Severity = Info → routed as Internal-only
        // by the notification system (no Discord alert per design §6).
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var metadata = new Dictionary<string, object?>
        {
            ["sprintId"] = sprint.Id,
            ["currentStage"] = sprint.CurrentStage,
            ["roundsThisSprint"] = sprint.RoundsThisSprint,
            ["roundsThisStage"] = sprint.RoundsThisStage,
            ["selfDriveContinuations"] = sprint.SelfDriveContinuations,
        };
        var entity = new ActivityEventEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = ActivityEventType.SprintRoundContinuationScheduled.ToString(),
            Severity = ActivitySeverity.Info.ToString(),
            RoomId = roomId,
            ActorId = null,
            TaskId = null,
            Message = $"Self-drive continuation scheduled (stage={sprint.CurrentStage}, " +
                      $"rounds={sprint.RoundsThisSprint}, stageRounds={sprint.RoundsThisStage})",
            CorrelationId = null,
            OccurredAt = DateTime.UtcNow,
            MetadataJson = JsonSerializer.Serialize(metadata),
        };
        db.ActivityEvents.Add(entity);
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _shutdownCts.Cancel(); } catch { /* ignore */ }
        _shutdownCts.Dispose();
    }
}
