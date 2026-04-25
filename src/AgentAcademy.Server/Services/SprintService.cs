using System.Collections.ObjectModel;
using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages sprint lifecycle: creation, completion, cancellation, and timeout queries.
/// Stage advancement logic lives in <see cref="SprintStageService"/>.
/// </summary>
public sealed class SprintService : Contracts.ISprintService
{
    /// <summary>Read-only view of the sprint stages, delegated to <see cref="SprintStageService"/>.</summary>
    public static ReadOnlyCollection<string> Stages => SprintStageService.Stages;

    private readonly AgentAcademyDbContext _db;
    private readonly IActivityBroadcaster _activityBus;
    private readonly ISystemSettingsService _settings;
    private readonly ILogger<SprintService> _logger;
    private readonly ISprintKickoffService? _kickoff;
    private readonly Contracts.IRoomLifecycleService? _roomLifecycle;
    private readonly Contracts.IWorkspaceRoomService? _workspaceRooms;

    public SprintService(
        AgentAcademyDbContext db,
        IActivityBroadcaster activityBus,
        ISystemSettingsService settings,
        ILogger<SprintService> logger,
        ISprintKickoffService? kickoff = null,
        Contracts.IRoomLifecycleService? roomLifecycle = null,
        Contracts.IWorkspaceRoomService? workspaceRooms = null)
    {
        _db = db;
        _activityBus = activityBus;
        _settings = settings;
        _logger = logger;
        _kickoff = kickoff;
        _roomLifecycle = roomLifecycle;
        _workspaceRooms = workspaceRooms;
    }

    // ── Create ───────────────────────────────────────────────────

    /// <summary>
    /// Creates the next sprint for a workspace. If a previous sprint exists and
    /// has overflow artifacts, they are linked via <see cref="SprintEntity.OverflowFromSprintId"/>.
    /// Throws if there is already an active sprint for this workspace.
    /// </summary>
    /// <param name="workspacePath">Absolute path to the workspace.</param>
    /// <param name="trigger">Optional trigger label included in the SprintStarted event metadata (e.g. "auto").</param>
    public async Task<SprintEntity> CreateSprintAsync(string workspacePath, string? trigger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var existing = await GetActiveSprintAsync(workspacePath);
        if (existing is not null)
            throw new InvalidOperationException(
                $"Workspace already has an active sprint (#{existing.Number}, id={existing.Id}). " +
                "Complete or cancel it before starting a new one.");

        var lastSprint = await _db.Sprints
            .Where(s => s.WorkspacePath == workspacePath)
            .OrderByDescending(s => s.Number)
            .FirstOrDefaultAsync();

        var nextNumber = (lastSprint?.Number ?? 0) + 1;

        // Check for overflow artifacts from the previous sprint
        string? overflowFrom = null;
        SprintArtifactEntity? overflowArtifact = null;
        if (lastSprint is not null)
        {
            overflowArtifact = await _db.SprintArtifacts
                .FirstOrDefaultAsync(a => a.SprintId == lastSprint.Id
                    && a.Stage == "FinalSynthesis"
                    && a.Type == "OverflowRequirements");
            if (overflowArtifact is not null)
                overflowFrom = lastSprint.Id;
        }

        var sprint = new SprintEntity
        {
            Id = Guid.NewGuid().ToString(),
            Number = nextNumber,
            WorkspacePath = workspacePath,
            Status = "Active",
            CurrentStage = Stages[0],
            OverflowFromSprintId = overflowFrom,
            CreatedAt = DateTime.UtcNow,
        };

        _db.Sprints.Add(sprint);

        // Auto-inject overflow requirements into the new sprint's Intake stage
        if (overflowArtifact is not null)
        {
            _db.SprintArtifacts.Add(new SprintArtifactEntity
            {
                SprintId = sprint.Id,
                Stage = "Intake",
                Type = "OverflowRequirements",
                Content = overflowArtifact.Content,
                CreatedByAgentId = null, // system-injected
                CreatedAt = DateTime.UtcNow,
            });
        }

        QueueEvent(ActivityEventType.SprintStarted, null, null, null,
            $"Sprint #{sprint.Number} started for workspace {workspacePath}",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprint.Id,
                ["sprintNumber"] = sprint.Number,
                ["status"] = "Active",
                ["currentStage"] = Stages[0],
                ["trigger"] = trigger, // null when manually started, "auto" when auto-started
            });

        // Sync existing rooms to the new sprint's initial stage. Without this,
        // rooms that were previously at e.g. Implementation keep that stale
        // CurrentPhase even though a fresh sprint just started at Intake,
        // and downstream room snapshot / stage roster filters apply the wrong
        // phase. Mirrors SprintStageService.SyncWorkspaceRoomsToStageAsync,
        // skipping Archived/Completed rooms.
        await SyncRoomsToInitialStageAsync(workspacePath, sprint.Id, Stages[0]);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Unique index violation from concurrent CreateSprintAsync calls.
            // Detach the failed entity so the context is usable for the re-query.
            _db.Entry(sprint).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            var conflict = await GetActiveSprintAsync(workspacePath);
            if (conflict is not null)
                throw new InvalidOperationException(
                    $"Workspace already has an active sprint (#{conflict.Number}, id={conflict.Id}). " +
                    "Complete or cancel it before starting a new one.");
            throw;
        }
        FlushEvents();

        _logger.LogInformation(
            "Created sprint #{Number} ({Id}) for workspace {Workspace}{Overflow}",
            sprint.Number, sprint.Id, workspacePath,
            overflowFrom is not null ? $" (overflow from {overflowFrom})" : "");

        // Post the kickoff coordination message and wake the orchestrator so
        // agents pick up the new sprint without further human input. Best-effort:
        // failures are logged inside PostKickoffAsync and never propagate.
        if (_kickoff is not null)
        {
            await _kickoff.PostKickoffAsync(sprint, trigger);
        }

        return sprint;
    }

    // ── Query ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the active sprint for a workspace, or null if none.
    /// </summary>
    public async Task<SprintEntity?> GetActiveSprintAsync(string workspacePath)
    {
        return await _db.Sprints
            .Where(s => s.WorkspacePath == workspacePath && s.Status == "Active")
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Returns a sprint by ID, or null if not found.
    /// </summary>
    public async Task<SprintEntity?> GetSprintByIdAsync(string sprintId)
    {
        return await _db.Sprints.FindAsync(sprintId);
    }

    /// <summary>
    /// Returns all sprints for a workspace, ordered by number descending.
    /// </summary>
    public async Task<(List<SprintEntity> Items, int TotalCount)> GetSprintsForWorkspaceAsync(
        string workspacePath, int limit = 20, int offset = 0)
    {
        var query = _db.Sprints.Where(s => s.WorkspacePath == workspacePath);
        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(s => s.Number)
            .Skip(Math.Max(offset, 0))
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync();
        return (items, totalCount);
    }

    // ── Stage Advancement (delegated to SprintStageService) ────

    // Stage advancement, approval, rejection, and sign-off timeout are handled by
    // SprintStageService. Callers should inject SprintStageService directly for
    // those operations. The following static helpers delegate for backward compatibility.

    /// <inheritdoc cref="SprintStageService.GetStageIndex"/>
    public static int GetStageIndex(string stage) => SprintStageService.GetStageIndex(stage);

    /// <inheritdoc cref="SprintStageService.GetNextStage"/>
    public static string? GetNextStage(string stage) => SprintStageService.GetNextStage(stage);

    // ── Completion ───────────────────────────────────────────────

    /// <summary>
    /// Marks a sprint as completed. Must be in the FinalSynthesis stage
    /// (or force=true to skip the stage check). If overflow requirements
    /// exist, they'll be picked up by the next sprint's creation.
    /// </summary>
    public async Task<SprintEntity> CompleteSprintAsync(string sprintId, bool force = false)
    {
        var sprint = await _db.Sprints.FindAsync(sprintId)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");

        if (sprint.Status != "Active")
            throw new InvalidOperationException(
                $"Sprint {sprintId} is already {sprint.Status}.");

        if (!force && sprint.CurrentStage != "FinalSynthesis")
            throw new InvalidOperationException(
                $"Cannot complete sprint {sprintId} — current stage is {sprint.CurrentStage}, " +
                "expected FinalSynthesis. Use force=true to override.");

        // Check for the final required artifact
        if (!force && SprintStageService.RequiredArtifactByStage.TryGetValue("FinalSynthesis", out var requiredType))
        {
            var hasArtifact = await _db.SprintArtifacts
                .AnyAsync(a => a.SprintId == sprintId
                    && a.Stage == "FinalSynthesis"
                    && a.Type == requiredType);

            if (!hasArtifact)
                throw new InvalidOperationException(
                    $"Cannot complete sprint: required artifact '{requiredType}' " +
                    "for FinalSynthesis has not been stored.");
        }

        sprint.Status = "Completed";
        sprint.CompletedAt = DateTime.UtcNow;
        // Terminal transitions clear the blocked flag — a terminated sprint
        // is no longer "paused waiting on a human"; the snapshot must not
        // expose contradictory state to API clients.
        sprint.BlockedAt = null;
        sprint.BlockReason = null;

        QueueEvent(ActivityEventType.SprintCompleted, null, null, null,
            $"Sprint #{sprint.Number} completed for workspace {sprint.WorkspacePath}",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["status"] = "Completed",
            });

        // Atomically persist the sprint completion AND freeze its workspace
        // rooms. Both must succeed or neither is committed — otherwise we get
        // the inconsistent state of a "Completed" sprint with rooms still
        // accepting writes (read-only guarantee broken). The transaction also
        // closes the race where a concurrent CreateSprintAsync could observe
        // the completion before rooms are frozen and start a new sprint that
        // gets swept up by the lifecycle pass.
        await PersistTerminalSprintWithRoomFreezeAsync(sprint);
        FlushEvents();

        _logger.LogInformation(
            "Completed sprint #{Number} ({Id}) for workspace {Workspace}",
            sprint.Number, sprint.Id, sprint.WorkspacePath);

        // Auto-start the next sprint if configured
        await TryAutoStartNextSprintAsync(sprint);

        return sprint;
    }

    /// <summary>
    /// Persists a sprint state change (Completed/Cancelled) AND freezes the
    /// sprint's workspace rooms inside a single transaction. Either both
    /// commit or neither does. Eliminates the "Completed sprint, Active room"
    /// inconsistency and the race where a concurrent CreateSprintAsync
    /// observes completion before rooms are frozen.
    ///
    /// Falls back to a plain SaveChanges when no <see cref="Contracts.IRoomLifecycleService"/>
    /// is wired (legacy test paths and any boot scenario where the lifecycle
    /// service hasn't been registered).
    /// </summary>
    private async Task PersistTerminalSprintWithRoomFreezeAsync(SprintEntity sprint)
    {
        if (_roomLifecycle is null)
        {
            await _db.SaveChangesAsync();
            return;
        }

        // SQLite in-memory test contexts can lack a transactional connection
        // wrapper; treat that as "best effort, still atomic on the same
        // SaveChanges" via shared DbContext.
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
        try
        {
            tx = await _db.Database.BeginTransactionAsync();
        }
        catch (InvalidOperationException)
        {
            // No relational provider — skip explicit transaction; SaveChanges
            // is still atomic and MarkSprintRoomsCompletedAsync below saves
            // both the sprint state change and the room status updates in
            // the same SaveChanges call.
        }

        try
        {
            // Save the sprint state change first within the transaction.
            await _db.SaveChangesAsync();

            // Then freeze rooms (calls SaveChanges itself). On the same
            // DbContext + transaction the work is atomic.
            await _roomLifecycle.MarkSprintRoomsCompletedAsync(
                sprint.WorkspacePath, sprint.Id);

            if (tx is not null)
                await tx.CommitAsync();
        }
        catch
        {
            if (tx is not null)
            {
                try { await tx.RollbackAsync(); }
                catch { /* best-effort rollback */ }
            }
            throw;
        }
        finally
        {
            tx?.Dispose();
        }
    }

    /// <summary>
    /// If the <c>sprint.autoStartOnCompletion</c> setting is enabled,
    /// creates the next sprint for the same workspace.
    /// Failures are logged as warnings — they never fail the completion itself.
    /// </summary>
    private async Task TryAutoStartNextSprintAsync(SprintEntity completedSprint)
    {
        try
        {
            var autoStart = await _settings.GetSprintAutoStartAsync();
            if (!autoStart)
                return;

            _logger.LogInformation(
                "Auto-start enabled — creating next sprint for workspace {Workspace}",
                completedSprint.WorkspacePath);

            // The previous sprint's room freeze moved every workspace room to
            // Completed. Provision a fresh default room for the next sprint
            // before CreateSprintAsync runs, otherwise
            // SyncRoomsToInitialStageAsync has nothing to sync and
            // PostKickoffAsync silently no-ops (it filters out Completed
            // rooms), leaving the auto-started sprint without a writable
            // surface.
            if (_workspaceRooms is not null)
            {
                try
                {
                    await _workspaceRooms.EnsureDefaultRoomForWorkspaceAsync(
                        completedSprint.WorkspacePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Auto-start: failed to ensure a fresh default room for workspace {Workspace}",
                        completedSprint.WorkspacePath);
                    return;
                }
            }

            var next = await CreateSprintAsync(completedSprint.WorkspacePath, trigger: "auto");

            _logger.LogInformation(
                "Auto-started sprint #{Number} ({Id}) after completion of sprint #{PrevNumber}",
                next.Number, next.Id, completedSprint.Number);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Auto-start failed after sprint #{Number} completion for workspace {Workspace}",
                completedSprint.Number, completedSprint.WorkspacePath);
        }
    }

    /// <summary>
    /// Cancels an active sprint.
    /// </summary>
    public async Task<SprintEntity> CancelSprintAsync(string sprintId)
    {
        var sprint = await _db.Sprints.FindAsync(sprintId)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");

        if (sprint.Status != "Active")
            throw new InvalidOperationException(
                $"Sprint {sprintId} is already {sprint.Status}.");

        sprint.Status = "Cancelled";
        sprint.CompletedAt = DateTime.UtcNow;
        // Terminal transitions clear the blocked flag (see CompleteSprintAsync).
        sprint.BlockedAt = null;
        sprint.BlockReason = null;

        QueueEvent(ActivityEventType.SprintCancelled, null, null, null,
            $"Sprint #{sprint.Number} cancelled for workspace {sprint.WorkspacePath}",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["status"] = "Cancelled",
            });

        // Atomic: cancel + room freeze commit together.
        await PersistTerminalSprintWithRoomFreezeAsync(sprint);
        FlushEvents();

        _logger.LogInformation(
            "Cancelled sprint #{Number} ({Id}) for workspace {Workspace}",
            sprint.Number, sprint.Id, sprint.WorkspacePath);

        return sprint;
    }

    // ── Blocked Signal ───────────────────────────────────────────

    /// <inheritdoc />
    public async Task<SprintEntity> MarkSprintBlockedAsync(string sprintId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = DateTime.UtcNow;

        // Atomic conditional transition: only set BlockedAt if currently null
        // AND status is Active. Eliminates the TOCTOU race where two callers
        // both observe BlockedAt==null and both emit a SprintBlocked event.
        var rowsTransitioned = await _db.Sprints
            .Where(s => s.Id == sprintId && s.Status == "Active" && s.BlockedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.BlockedAt, now)
                .SetProperty(s => s.BlockReason, reason));

        // Re-read after ExecuteUpdate (which bypasses the change tracker).
        var sprint = await _db.Sprints.FindAsync(sprintId)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");
        await _db.Entry(sprint).ReloadAsync();

        if (rowsTransitioned == 0)
        {
            // Either not Active, or already blocked.
            if (sprint.Status != "Active")
                throw new InvalidOperationException(
                    $"Cannot block sprint {sprintId} — status is {sprint.Status}.");

            // Already blocked — update the reason silently (no second event)
            // so reason updates don't spam the human with notifications.
            // Use a conditional ExecuteUpdate so a concurrent unblock or
            // terminal transition can't leave us with BlockReason!=null on a
            // sprint that's no longer blocked (which would resurrect the
            // contradictory state the terminal-clear logic eliminates).
            if (sprint.BlockReason != reason)
            {
                var rowsReasonUpdated = await _db.Sprints
                    .Where(s => s.Id == sprintId
                        && s.Status == "Active"
                        && s.BlockedAt != null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.BlockReason, reason));

                await _db.Entry(sprint).ReloadAsync();

                if (rowsReasonUpdated == 0)
                {
                    if (sprint.Status != "Active")
                        throw new InvalidOperationException(
                            $"Cannot block sprint {sprintId} — status is {sprint.Status}.");
                    // Concurrent unblock cleared BlockedAt; nothing to do.
                    return sprint;
                }

                _logger.LogInformation(
                    "Sprint #{Number} ({Id}) blocked-reason updated: {Reason}",
                    sprint.Number, sprint.Id, reason);
            }
            return sprint;
        }

        // Won the transition race — emit exactly once.
        QueueEvent(ActivityEventType.SprintBlocked, null, null, null,
            $"Sprint #{sprint.Number} blocked: {reason}",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["reason"] = reason,
            });
        // Persist the queued ActivityEventEntity. ExecuteUpdateAsync above
        // bypassed the change tracker, so the activity row only lands if we
        // flush the tracker explicitly. Without this, the audit log misses
        // SprintBlocked even though the in-memory broadcaster fires.
        await _db.SaveChangesAsync();
        FlushEvents();

        _logger.LogWarning(
            "Sprint #{Number} ({Id}) marked Blocked: {Reason}",
            sprint.Number, sprint.Id, reason);

        return sprint;
    }

    /// <inheritdoc />
    public async Task<SprintEntity> UnblockSprintAsync(string sprintId)
    {
        // Snapshot previous state BEFORE the conditional update so we can
        // include the previous reason in the event regardless of whether the
        // entity was already tracked in this DbContext.
        var preState = await _db.Sprints
            .AsNoTracking()
            .Where(s => s.Id == sprintId)
            .Select(s => new { s.Status, s.BlockReason, s.BlockedAt })
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");

        // Atomic conditional clear: only succeeds if currently blocked.
        var rowsTransitioned = await _db.Sprints
            .Where(s => s.Id == sprintId && s.Status == "Active" && s.BlockedAt != null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.BlockedAt, (DateTime?)null)
                .SetProperty(s => s.BlockReason, (string?)null));

        var sprint = await _db.Sprints.FindAsync(sprintId)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");
        await _db.Entry(sprint).ReloadAsync();

        if (rowsTransitioned == 0)
        {
            // Not blocked OR not Active. Use the freshly-reloaded status so we
            // correctly throw if the sprint terminated between the snapshot
            // and the conditional update (TOCTOU).
            if (sprint.Status != "Active")
                throw new InvalidOperationException(
                    $"Cannot unblock sprint {sprintId} — status is {sprint.Status}.");
            return sprint; // Idempotent no-op (was never blocked).
        }

        QueueEvent(ActivityEventType.SprintUnblocked, null, null, null,
            $"Sprint #{sprint.Number} unblocked",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["previousReason"] = preState.BlockReason,
            });
        // Persist the queued ActivityEventEntity (see MarkSprintBlockedAsync
        // for rationale — ExecuteUpdateAsync bypasses the tracker).
        await _db.SaveChangesAsync();
        FlushEvents();

        _logger.LogInformation(
            "Sprint #{Number} ({Id}) unblocked", sprint.Number, sprint.Id);

        return sprint;
    }

    // ── Timeout Queries ──────────────────────────────────────────

    /// <summary>
    /// Returns active sprints that have been in AwaitingSignOff longer than the specified timeout.
    /// Excludes sprints flagged as Blocked — those are explicitly paused waiting
    /// on a human and must not have their pending sign-off auto-rejected.
    /// </summary>
    public async Task<List<SprintEntity>> GetTimedOutSignOffSprintsAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - timeout;
        return await _db.Sprints
            .Where(s => s.Status == "Active"
                && s.BlockedAt == null
                && s.AwaitingSignOff
                && s.SignOffRequestedAt != null
                && s.SignOffRequestedAt < cutoff)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns active sprints whose total duration exceeds the specified limit.
    /// Excludes sprints flagged as Blocked — those are explicitly waiting on a
    /// human and must not be auto-cancelled by the timeout sweep.
    /// </summary>
    public async Task<List<SprintEntity>> GetOverdueSprintsAsync(TimeSpan maxDuration, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - maxDuration;
        return await _db.Sprints
            .Where(s => s.Status == "Active" && s.BlockedAt == null && s.CreatedAt < cutoff)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Auto-cancels a sprint that has exceeded the maximum duration.
    /// </summary>
    public async Task<SprintEntity> TimeOutSprintAsync(string sprintId, CancellationToken ct = default)
    {
        var sprint = await _db.Sprints.FindAsync([sprintId], ct)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");

        if (sprint.Status != "Active")
            throw new InvalidOperationException(
                $"Sprint {sprintId} is already {sprint.Status}.");

        sprint.Status = "Cancelled";
        sprint.CompletedAt = DateTime.UtcNow;
        sprint.AwaitingSignOff = false;
        sprint.PendingStage = null;
        sprint.SignOffRequestedAt = null;
        // Terminal transitions clear the blocked flag (see CompleteSprintAsync).
        sprint.BlockedAt = null;
        sprint.BlockReason = null;

        QueueEvent(ActivityEventType.SprintCancelled, null, null, null,
            $"Sprint #{sprint.Number} auto-cancelled — exceeded maximum duration",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["status"] = "Cancelled",
                ["reason"] = "timeout",
            });

        // Atomic: timeout-cancel + room freeze commit together.
        await PersistTerminalSprintWithRoomFreezeAsync(sprint);
        FlushEvents();

        _logger.LogWarning(
            "Sprint #{Number} ({Id}) auto-cancelled — exceeded maximum duration",
            sprint.Number, sprint.Id);

        return sprint;
    }

    // ── Self-Drive Counters (P1.2 §13 step 3) ────────────────────

    /// <inheritdoc />
    public async Task<int> IncrementRoundCountersAsync(
        string sprintId,
        int innerRoundsExecuted,
        bool wasSelfDriveContinuation,
        DateTime completedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sprintId);

        if (innerRoundsExecuted <= 0)
        {
            // Trigger ran but produced no inner rounds (no agents available,
            // immediate exit, etc.). Still update LastRoundCompletedAt so ops
            // can see the trigger happened, but do not bump round counters
            // (they should reflect actual agent turns, not trigger arrivals).
            return await _db.Sprints
                .Where(s => s.Id == sprintId && s.Status == "Active")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.LastRoundCompletedAt, completedAt), ct);
        }

        // Atomic counter bump: only Active sprints accumulate. Blocked sprints
        // are still Status="Active" by P1.4 design, so they DO accumulate
        // counters if a round somehow ran on them — but design principle 7
        // forbids that, so it should not happen. The counter still increments
        // here as a defensive paper trail; the decision service is responsible
        // for not triggering rounds on blocked sprints in the first place.
        var rowsUpdated = await _db.Sprints
            .Where(s => s.Id == sprintId && s.Status == "Active")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.RoundsThisSprint, s => s.RoundsThisSprint + innerRoundsExecuted)
                .SetProperty(s => s.RoundsThisStage, s => s.RoundsThisStage + innerRoundsExecuted)
                .SetProperty(s => s.SelfDriveContinuations,
                    s => wasSelfDriveContinuation
                        ? s.SelfDriveContinuations + 1
                        : s.SelfDriveContinuations)
                .SetProperty(s => s.LastRoundCompletedAt, completedAt), ct);

        return rowsUpdated;
    }

    // ── Event Helpers ────────────────────────────────────────────

    /// <summary>
    /// Syncs all rooms in the workspace to the new sprint's initial stage,
    /// queuing PhaseChanged events. Mirrors
    /// <c>SprintStageService.SyncWorkspaceRoomsToStageAsync</c> but is invoked
    /// at sprint creation time so a fresh sprint at Intake correctly resets
    /// rooms whose CurrentPhase still reflects a prior sprint's later stage.
    /// Skips Archived and Completed rooms so the sync cannot revive terminal
    /// state. Queues changes onto the same EF change-tracker as the sprint
    /// row; the caller is responsible for SaveChangesAsync + FlushEvents.
    /// </summary>
    private async Task SyncRoomsToInitialStageAsync(string workspacePath, string sprintId, string newStage)
    {
        if (string.IsNullOrEmpty(workspacePath)) return;

        var archivedStatus = nameof(RoomStatus.Archived);
        var completedStatus = nameof(RoomStatus.Completed);

        var rooms = await _db.Rooms
            .Where(r => r.WorkspacePath == workspacePath
                && r.CurrentPhase != newStage
                && r.Status != archivedStatus
                && r.Status != completedStatus)
            .ToListAsync();

        if (rooms.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var room in rooms)
        {
            var oldPhase = room.CurrentPhase;
            room.CurrentPhase = newStage;
            room.UpdatedAt = now;

            QueueEvent(ActivityEventType.PhaseChanged, room.Id, null, null,
                $"Room '{room.Name}' phase synced to new sprint stage: {oldPhase} → {newStage}",
                new Dictionary<string, object?>
                {
                    ["roomId"] = room.Id,
                    ["previousPhase"] = oldPhase,
                    ["currentPhase"] = newStage,
                    ["source"] = "sprint-create",
                    ["sprintId"] = sprintId,
                });
        }

        _logger.LogInformation(
            "Sprint create synced {Count} room(s) in workspace '{Workspace}' to stage '{Stage}'",
            rooms.Count, workspacePath, newStage);
    }

    private readonly List<ActivityEvent> _pendingEvents = [];

    private void QueueEvent(
        ActivityEventType type, string? roomId, string? actorId, string? taskId, string message,
        Dictionary<string, object?>? metadata = null)
    {
        var evt = new ActivityEvent(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            Severity: ActivitySeverity.Info,
            RoomId: roomId,
            ActorId: actorId,
            TaskId: taskId,
            Message: message,
            CorrelationId: null,
            OccurredAt: DateTime.UtcNow,
            Metadata: metadata
        );

        _db.ActivityEvents.Add(new ActivityEventEntity
        {
            Id = evt.Id,
            Type = evt.Type.ToString(),
            Severity = evt.Severity.ToString(),
            RoomId = evt.RoomId,
            ActorId = evt.ActorId,
            TaskId = evt.TaskId,
            Message = evt.Message,
            CorrelationId = evt.CorrelationId,
            OccurredAt = evt.OccurredAt,
            MetadataJson = metadata is not null ? JsonSerializer.Serialize(metadata) : null,
        });

        _pendingEvents.Add(evt);
    }

    /// <summary>
    /// Broadcasts all queued events. Call AFTER SaveChangesAsync to ensure
    /// subscribers never see events for uncommitted data.
    /// </summary>
    private void FlushEvents()
    {
        foreach (var evt in _pendingEvents)
            _activityBus.Broadcast(evt);
        _pendingEvents.Clear();
    }
}
