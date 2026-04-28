using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Drives the sprint terminal-stage ceremony chain. After every agent round,
/// classifies the captured sprint's terminal-stage state (per design §3) and
/// fires at most ONE ceremony transition per invocation:
/// <list type="number">
///   <item><c>StartedSelfEval</c> — flips <c>SelfEvaluationInFlight=true</c>, stamps <c>SelfEvalStartedAt</c>, wakes rooms.</item>
///   <item><c>AdvancedToFinalSynthesis</c> — calls <see cref="ISprintStageService.AdvanceStageAsync"/> with <c>force=false</c>, wakes rooms.</item>
///   <item><c>SteeredToFinalSynthesis</c> — wakes rooms so the FinalSynthesis preamble drives agents to produce <c>SprintReport</c>; ticks watchdog.</item>
///   <item><c>CompletedSprint</c> — calls <see cref="ISprintService.CompleteSprintAsync"/> with <c>force=false</c>.</item>
/// </list>
/// Stateless. Idempotent on retry. Fails open: any unhandled exception is
/// logged and converted to <see cref="TerminalStageAction.NoOp"/> so the
/// round runner is never disrupted. See
/// <c>specs/100-product-vision/sprint-terminal-stage-handler-design.md</c>.
/// </summary>
public sealed class SprintTerminalStageHandler : ISprintTerminalStageHandler
{
    private readonly AgentAcademyDbContext _db;
    private readonly ISprintService _sprintService;
    private readonly ISprintStageService _stageService;
    private readonly ISprintArtifactService _artifactService;
    private readonly ITaskQueryService _taskQueryService;
    private readonly IOrchestratorWakeService _wakeService;
    private readonly TerminalStageOptions _options;
    private readonly ILogger<SprintTerminalStageHandler> _logger;
    private readonly TimeProvider _clock;

    public SprintTerminalStageHandler(
        AgentAcademyDbContext db,
        ISprintService sprintService,
        ISprintStageService stageService,
        ISprintArtifactService artifactService,
        ITaskQueryService taskQueryService,
        IOrchestratorWakeService wakeService,
        ILogger<SprintTerminalStageHandler> logger,
        IOptions<TerminalStageOptions>? options = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _sprintService = sprintService;
        _stageService = stageService;
        _artifactService = artifactService;
        _taskQueryService = taskQueryService;
        _wakeService = wakeService;
        _options = options?.Value ?? new TerminalStageOptions();
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<TerminalStageAction> AdvanceIfReadyAsync(string sprintId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sprintId)) return TerminalStageAction.NoOp;

        try
        {
            // Read sprint state with AsNoTracking so the driver always sees
            // the freshest committed state — even if a long-lived DbContext
            // has older versions in its change tracker (a concern for tests
            // and for any future DI re-wiring that uses a non-per-round
            // scope). Production wires the driver per-scope per-round so the
            // tracker is always empty here, but this keeps us correct under
            // either lifetime model.
            var sprint = await LoadSprintFreshAsync(sprintId, ct);
            if (sprint is null) return TerminalStageAction.NoOp;

            // State: NotApplicable
            if (sprint.Status != "Active"
                || sprint.BlockedAt is not null
                || sprint.AwaitingSignOff)
            {
                return TerminalStageAction.NoOp;
            }

            if (string.Equals(sprint.CurrentStage, "Implementation", StringComparison.Ordinal))
            {
                return await ClassifyImplementationAsync(sprint, ct);
            }

            if (string.Equals(sprint.CurrentStage, "FinalSynthesis", StringComparison.Ordinal))
            {
                return await ClassifyFinalSynthesisAsync(sprint, ct);
            }

            return TerminalStageAction.NoOp;
        }
        catch (Exception ex)
        {
            // Fail-open: a driver failure must NEVER propagate into the round
            // runner. Log and convert to NoOp; next round's invocation
            // re-evaluates state and either advances or blocks if the failure
            // is structural.
            _logger.LogWarning(ex,
                "SprintTerminalStageHandler.AdvanceIfReadyAsync failed for sprint {SprintId}; " +
                "ceremony chain will retry on next round (fail-open)",
                sprintId);
            return TerminalStageAction.NoOp;
        }
    }

    // ── Classifiers ─────────────────────────────────────────────

    private async Task<TerminalStageAction> ClassifyImplementationAsync(
        SprintEntity sprint, CancellationToken ct)
    {
        // Predicate: every task in {Completed, Cancelled}, ≥1 non-cancelled.
        // Matches RoomLifecycleService.TerminalTaskStatuses exactly so the
        // driver and CheckImplementationPrerequisitesAsync can never disagree.
        var snapshot = await _taskQueryService.GetSprintTaskStatusSnapshotAsync(sprint.Id, ct);
        if (!snapshot.AllTerminal || snapshot.NonCancelledCount == 0)
        {
            // ImplementationInProgress — let normal self-drive continue.
            return TerminalStageAction.NoOp;
        }

        // ReadyForStageAdvance takes priority over ReadyForSelfEval: if a
        // verdict is already AllPass, advance the stage; the in-flight flag
        // is irrelevant in that case (verdict path will have cleared it).
        var verdict = await _artifactService.GetLatestSelfEvalVerdictAsync(sprint.Id, ct);
        if (verdict == SelfEvaluationOverallVerdict.AllPass)
        {
            return await TryAdvanceStageAsync(sprint, ct);
        }

        if (sprint.SelfEvaluationInFlight)
        {
            // SelfEvalInFlight — wait for verdict to land. Tick watchdog.
            return await TickSelfEvalWatchdogAsync(sprint, ct);
        }

        // ReadyForSelfEval — fire RUN_SELF_EVAL.
        return await TryStartSelfEvalAsync(sprint, ct);
    }

    private async Task<TerminalStageAction> ClassifyFinalSynthesisAsync(
        SprintEntity sprint, CancellationToken ct)
    {
        var hasReport = await _artifactService.HasArtifactAsync(
            sprint.Id, stage: "FinalSynthesis", type: nameof(ArtifactType.SprintReport), ct);
        if (hasReport)
        {
            // ReadyForCompletion — fire CompleteSprintAsync(force: false).
            return await TryCompleteSprintAsync(sprint, ct);
        }

        // FinalSynthesisInProgress — wake orchestrator AND tick watchdog.
        return await SteerToFinalSynthesisAsync(sprint, ct);
    }

    // ── Action helpers (each isolated by stale-state classifier) ──

    private async Task<TerminalStageAction> TryStartSelfEvalAsync(
        SprintEntity sprint, CancellationToken ct)
    {
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;

            // Atomic flip mirroring RunSelfEvalHandler's ExecuteUpdateAsync
            // pattern (Commands/Handlers/RunSelfEvalHandler.cs:167-174). Same
            // guard set: still Active, not blocked, still at Implementation,
            // not already in-flight. Adds SelfEvalStartedAt stamp in the same
            // atomic write so the watchdog has a baseline that can't race the
            // flip. A loser of the concurrent-driver-invocation race writes
            // zero rows — silent NoOp, next round re-classifies.
            var rowsFlipped = await _db.Sprints
                .Where(s => s.Id == sprint.Id
                    && s.Status == "Active"
                    && s.BlockedAt == null
                    && s.CurrentStage == "Implementation"
                    && !s.SelfEvaluationInFlight)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.SelfEvaluationInFlight, true)
                    .SetProperty(s => s.SelfEvalStartedAt, now), ct);

            if (rowsFlipped == 0)
            {
                _logger.LogInformation(
                    "TryStartSelfEvalAsync stale-state for sprint {SprintId} #{Number}: " +
                    "concurrent transition won the race — treating as NoOp",
                    sprint.Id, sprint.Number);
                return TerminalStageAction.NoOp;
            }

            await _wakeService.WakeWorkspaceRoomsForSprintAsync(sprint.Id, ct);
            LogAction(sprint, TerminalStageAction.StartedSelfEval, nextStage: "Implementation");
            return TerminalStageAction.StartedSelfEval;
        }
        catch (Exception ex)
        {
            await BlockWithReasonAsync(sprint, $"start-self-eval: {ex.Message}", ct);
            return TerminalStageAction.Blocked;
        }
    }

    private async Task<TerminalStageAction> TryAdvanceStageAsync(
        SprintEntity sprint, CancellationToken ct)
    {
        try
        {
            await _stageService.AdvanceStageAsync(sprint.Id, force: false);
            // Re-read with AsNoTracking so we observe the post-advance flags
            // (AwaitingSignOff when an operator configured Implementation in
            // SignOffRequiredStages) even when the DbContext change tracker
            // has stale entries.
            var post = await LoadSprintFreshAsync(sprint.Id, ct);
            if (post?.AwaitingSignOff == true)
            {
                LogAction(sprint, TerminalStageAction.RequestedSignOff, nextStage: "FinalSynthesis");
                return TerminalStageAction.RequestedSignOff;
            }

            await _wakeService.WakeWorkspaceRoomsForSprintAsync(sprint.Id, ct);
            LogAction(sprint, TerminalStageAction.AdvancedToFinalSynthesis, nextStage: "FinalSynthesis");
            return TerminalStageAction.AdvancedToFinalSynthesis;
        }
        catch (InvalidOperationException ex) when (IsStaleStateException(ex))
        {
            _logger.LogInformation(
                "AdvanceStageAsync stale-state for sprint {SprintId} #{Number}: {Message} — treating as NoOp",
                sprint.Id, sprint.Number, ex.Message);
            return TerminalStageAction.NoOp;
        }
        catch (Exception ex)
        {
            await BlockWithReasonAsync(sprint, $"advance-stage: {ex.Message}", ct);
            return TerminalStageAction.Blocked;
        }
    }

    private async Task<TerminalStageAction> TryCompleteSprintAsync(
        SprintEntity sprint, CancellationToken ct)
    {
        try
        {
            await _sprintService.CompleteSprintAsync(sprint.Id, force: false);
            LogAction(sprint, TerminalStageAction.CompletedSprint, nextStage: "FinalSynthesis");
            return TerminalStageAction.CompletedSprint;
        }
        catch (InvalidOperationException ex) when (IsStaleStateException(ex))
        {
            _logger.LogInformation(
                "CompleteSprintAsync stale-state for sprint {SprintId} #{Number}: {Message} — treating as NoOp",
                sprint.Id, sprint.Number, ex.Message);
            return TerminalStageAction.NoOp;
        }
        catch (Exception ex)
        {
            await BlockWithReasonAsync(sprint, $"complete-sprint: {ex.Message}", ct);
            return TerminalStageAction.Blocked;
        }
    }

    private async Task<TerminalStageAction> SteerToFinalSynthesisAsync(
        SprintEntity sprint, CancellationToken ct)
    {
        // Watchdog ticks BEFORE the wake — if the watchdog fires, the sprint
        // is blocked and there's no point waking rooms.
        var stallMinutes = _options.FinalSynthesisStallMinutes;
        if (stallMinutes > 0 && sprint.FinalSynthesisEnteredAt is { } enteredAt)
        {
            var elapsed = _clock.GetUtcNow().UtcDateTime - enteredAt;
            if (elapsed >= TimeSpan.FromMinutes(stallMinutes))
            {
                await BlockWithReasonAsync(sprint,
                    $"SprintReport not produced within {stallMinutes} minutes of entering FinalSynthesis.",
                    ct);
                return TerminalStageAction.Blocked;
            }
        }

        await _wakeService.WakeWorkspaceRoomsForSprintAsync(sprint.Id, ct);
        LogAction(sprint, TerminalStageAction.SteeredToFinalSynthesis, nextStage: "FinalSynthesis");
        return TerminalStageAction.SteeredToFinalSynthesis;
    }

    private async Task<TerminalStageAction> TickSelfEvalWatchdogAsync(
        SprintEntity sprint, CancellationToken ct)
    {
        var stallMinutes = _options.SelfEvalStallMinutes;
        if (stallMinutes <= 0) return TerminalStageAction.NoOp;

        // Watchdog uses max(SelfEvalStartedAt, LastSelfEvalAt). For the very
        // first attempt, LastSelfEvalAt is null and SelfEvalStartedAt is the
        // baseline. After AnyFail/Unverified, LastSelfEvalAt advances and
        // SelfEvalStartedAt is RE-stamped on the next StartedSelfEval, so the
        // window is always anchored to the most-recent self-eval start.
        DateTime? baseline = null;
        if (sprint.SelfEvalStartedAt is { } started) baseline = started;
        if (sprint.LastSelfEvalAt is { } last && (baseline is null || last > baseline))
        {
            baseline = last;
        }
        if (baseline is null)
        {
            // Sprint says SelfEvaluationInFlight=true but neither timestamp is
            // set. This can only happen if RUN_SELF_EVAL was invoked before
            // the driver was first wired (the controller path sets in-flight
            // but not SelfEvalStartedAt). Defensive NoOp; the next agent
            // round will produce a verdict and clear in-flight via the
            // existing P1.4 path, or the operator unblocks manually.
            return TerminalStageAction.NoOp;
        }

        var elapsed = _clock.GetUtcNow().UtcDateTime - baseline.Value;
        if (elapsed >= TimeSpan.FromMinutes(stallMinutes))
        {
            await BlockWithReasonAsync(sprint,
                $"SelfEvaluationReport not produced within {stallMinutes} minutes of self-eval start.",
                ct);
            return TerminalStageAction.Blocked;
        }
        return TerminalStageAction.NoOp;
    }

    // ── Block helper ────────────────────────────────────────────

    private async Task BlockWithReasonAsync(SprintEntity sprint, string reason, CancellationToken ct)
    {
        var fullReason = $"Terminal-stage ceremony failed: {reason}";
        try
        {
            await _sprintService.MarkSprintBlockedAsync(sprint.Id, fullReason);
            _logger.LogWarning(
                "SprintTerminalStageHandler blocked sprint {SprintId} #{Number}: {Reason}",
                sprint.Id, sprint.Number, fullReason);
        }
        catch (Exception ex)
        {
            // Block call itself failed — last-resort log. Next round will
            // re-classify; if the sprint is in a bad state the next driver
            // invocation may try to block again. Acceptable: the alternative
            // is an unbounded retry loop here. MarkSprintBlockedAsync uses
            // ExecuteUpdateAsync (SprintService.cs:451-455) so if the row
            // already shows BlockedAt the second attempt is a clean no-op.
            _logger.LogError(ex,
                "SprintTerminalStageHandler failed to block sprint {SprintId} #{Number} with reason '{Reason}'; " +
                "next round will re-attempt classification",
                sprint.Id, sprint.Number, fullReason);
        }
    }

    // ── Stale-state classifier ──────────────────────────────────

    /// <summary>
    /// Read sprint state bypassing the change tracker so the driver always
    /// observes the freshest committed row — including post-ExecuteUpdate
    /// writes that don't refresh tracked entities.
    /// </summary>
    private Task<SprintEntity?> LoadSprintFreshAsync(string sprintId, CancellationToken ct) =>
        _db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sprintId, ct);

    /// <summary>
    /// Classifies <see cref="InvalidOperationException"/> messages thrown by
    /// <see cref="ISprintService"/> / <see cref="ISprintStageService"/>
    /// preconditions. <b>A benign race ⇒ NoOp; a structural failure ⇒ Block.</b>
    /// Match list is narrow and explicit; new stale-state messages from
    /// those services must be added here in lockstep. See design §4.2.2.
    /// </summary>
    internal static bool IsStaleStateException(InvalidOperationException ex)
    {
        var msg = ex.Message;
        // SprintStageService.AdvanceStageAsync — "status is {X}" / "is awaiting user sign-off" / "already at the final stage"
        // SprintService.CompleteSprintAsync     — "Sprint X is already {Completed|Cancelled}"
        // SprintService.MarkSprintBlockedAsync  — "Sprint X is not Active" (handled by NotApplicable predicate, but defensive)
        return msg.Contains("status is", StringComparison.Ordinal)
            || msg.Contains("already at the final stage", StringComparison.Ordinal)
            || msg.Contains("awaiting user sign-off", StringComparison.Ordinal)
            || msg.Contains("is already", StringComparison.Ordinal);
    }

    private void LogAction(SprintEntity sprint, TerminalStageAction action, string nextStage)
    {
        _logger.LogInformation(
            "SprintTerminalStageHandler advanced sprint {SprintId} #{Number}: {Action} " +
            "({CurrentStage} → {NextStage}; selfEvalAttempts={Attempts}; verdict={Verdict})",
            sprint.Id, sprint.Number, action, sprint.CurrentStage, nextStage,
            sprint.SelfEvalAttempts, sprint.LastSelfEvalVerdict ?? "(none)");
    }
}
