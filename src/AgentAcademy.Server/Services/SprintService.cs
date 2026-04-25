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

    public SprintService(
        AgentAcademyDbContext db,
        IActivityBroadcaster activityBus,
        ISystemSettingsService settings,
        ILogger<SprintService> logger,
        ISprintKickoffService? kickoff = null)
    {
        _db = db;
        _activityBus = activityBus;
        _settings = settings;
        _logger = logger;
        _kickoff = kickoff;
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

        QueueEvent(ActivityEventType.SprintCompleted, null, null, null,
            $"Sprint #{sprint.Number} completed for workspace {sprint.WorkspacePath}",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["status"] = "Completed",
            });

        await _db.SaveChangesAsync();
        FlushEvents();

        _logger.LogInformation(
            "Completed sprint #{Number} ({Id}) for workspace {Workspace}",
            sprint.Number, sprint.Id, sprint.WorkspacePath);

        // Auto-start the next sprint if configured
        await TryAutoStartNextSprintAsync(sprint);

        return sprint;
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

        QueueEvent(ActivityEventType.SprintCancelled, null, null, null,
            $"Sprint #{sprint.Number} cancelled for workspace {sprint.WorkspacePath}",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["status"] = "Cancelled",
            });

        await _db.SaveChangesAsync();
        FlushEvents();

        _logger.LogInformation(
            "Cancelled sprint #{Number} ({Id}) for workspace {Workspace}",
            sprint.Number, sprint.Id, sprint.WorkspacePath);

        return sprint;
    }

    // ── Timeout Queries ──────────────────────────────────────────

    /// <summary>
    /// Returns active sprints that have been in AwaitingSignOff longer than the specified timeout.
    /// </summary>
    public async Task<List<SprintEntity>> GetTimedOutSignOffSprintsAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - timeout;
        return await _db.Sprints
            .Where(s => s.Status == "Active"
                && s.AwaitingSignOff
                && s.SignOffRequestedAt != null
                && s.SignOffRequestedAt < cutoff)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns active sprints whose total duration exceeds the specified limit.
    /// </summary>
    public async Task<List<SprintEntity>> GetOverdueSprintsAsync(TimeSpan maxDuration, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - maxDuration;
        return await _db.Sprints
            .Where(s => s.Status == "Active" && s.CreatedAt < cutoff)
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

        QueueEvent(ActivityEventType.SprintCancelled, null, null, null,
            $"Sprint #{sprint.Number} auto-cancelled — exceeded maximum duration",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["status"] = "Cancelled",
                ["reason"] = "timeout",
            });

        await _db.SaveChangesAsync(ct);
        FlushEvents();

        _logger.LogWarning(
            "Sprint #{Number} ({Id}) auto-cancelled — exceeded maximum duration",
            sprint.Number, sprint.Id);

        return sprint;
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
