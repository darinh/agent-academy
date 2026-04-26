using System.Collections.ObjectModel;
using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages the sprint stage state machine: advancement, sign-off gating,
/// approval/rejection. Owns the stage definitions and required-artifact gates.
/// Extracted from SprintService to separate stage logic from sprint lifecycle.
/// </summary>
public sealed class SprintStageService : ISprintStageService
{
    /// <summary>
    /// Ordered stages of a sprint. Advancement follows this sequence.
    /// </summary>
    private static readonly string[] StagesArray =
    [
        "Intake",
        "Planning",
        "Discussion",
        "Validation",
        "Implementation",
        "FinalSynthesis",
    ];

    /// <summary>
    /// Read-only view of the sprint stages. Cannot be mutated by callers.
    /// </summary>
    public static readonly ReadOnlyCollection<string> Stages = Array.AsReadOnly(StagesArray);

    /// <summary>
    /// Artifact types that must exist before leaving a stage.
    /// Stages not listed here have no mandatory artifact gate.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RequiredArtifactByStage =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Intake"] = "RequirementsDocument",
            ["Planning"] = "SprintPlan",
            ["Validation"] = "ValidationReport",
            ["Implementation"] = "SelfEvaluationReport",
            ["FinalSynthesis"] = "SprintReport",
        };

    /// <summary>
    /// Stages that require user sign-off before advancing.
    /// When an agent triggers ADVANCE_STAGE from one of these stages,
    /// the sprint enters AwaitingSignOff state until a human approves.
    /// </summary>
    /// <remarks>
    /// Bound from <see cref="SprintStageOptions.SignOffRequiredStages"/>.
    /// <b>Defaults to empty</b> — every stage auto-advances once gates pass.
    /// Operators opt into specific stages via <c>appsettings.json</c>.
    /// Tests that need a gate construct the service with an explicit
    /// <c>SprintStageOptions</c> instance.
    /// </remarks>
    private readonly IReadOnlySet<string> _signOffRequiredStages;

    private readonly AgentAcademyDbContext _db;
    private readonly IActivityBroadcaster _activityBus;
    private readonly ISprintStageAdvanceAnnouncer? _announcer;
    private readonly ILogger<SprintStageService> _logger;
    // Optional dependency for strict ID-based main-room exemption (B1).
    // When null, SyncWorkspaceRoomsToStageAsync falls back to the name-suffix
    // heuristic. Production DI always wires this.
    private readonly IRoomLifecycleService? _lifecycle;

    public SprintStageService(
        AgentAcademyDbContext db,
        IActivityBroadcaster activityBus,
        ILogger<SprintStageService> logger,
        ISprintStageAdvanceAnnouncer? announcer = null,
        IOptions<SprintStageOptions>? options = null,
        IRoomLifecycleService? lifecycle = null)
    {
        _db = db;
        _activityBus = activityBus;
        _logger = logger;
        _announcer = announcer;
        _lifecycle = lifecycle;

        var configured = options?.Value.SignOffRequiredStages ?? [];
        _signOffRequiredStages = new HashSet<string>(configured, StringComparer.Ordinal);
    }

    // ── Stage Advancement ────────────────────────────────────────

    /// <summary>
    /// Advances the sprint to the next stage. Validates artifact gates,
    /// stage prerequisites (e.g., all tasks completed), and sign-off gates.
    /// Use <paramref name="force"/> to skip prerequisite checks when a
    /// human decides to advance despite incomplete tasks.
    /// </summary>
    /// <param name="sprintId">Sprint to advance.</param>
    /// <param name="force">
    /// When true, skips stage prerequisites (task completion checks) but
    /// NOT artifact gates or sign-off requirements.
    /// </param>
    public async Task<SprintEntity> AdvanceStageAsync(string sprintId, bool force = false)
    {
        var sprint = await _db.Sprints.FindAsync(sprintId)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");

        if (sprint.Status != "Active")
            throw new InvalidOperationException(
                $"Cannot advance sprint {sprintId} — status is {sprint.Status}.");

        if (sprint.AwaitingSignOff)
            throw new InvalidOperationException(
                $"Sprint {sprintId} is awaiting user sign-off to advance from {sprint.CurrentStage}. " +
                "A human must approve before the stage can change.");

        var currentIndex = Stages.IndexOf(sprint.CurrentStage);
        if (currentIndex < 0)
            throw new InvalidOperationException(
                $"Sprint {sprintId} is in unknown stage '{sprint.CurrentStage}'.");

        if (currentIndex >= Stages.Count - 1)
            throw new InvalidOperationException(
                $"Sprint {sprintId} is already at the final stage ({sprint.CurrentStage}). " +
                "Use CompleteSprintAsync to finish it.");

        // Artifact gates — always enforced, even with force=true
        if (RequiredArtifactByStage.TryGetValue(sprint.CurrentStage, out var requiredType))
        {
            var hasArtifact = await _db.SprintArtifacts
                .AnyAsync(a => a.SprintId == sprintId
                    && a.Stage == sprint.CurrentStage
                    && a.Type == requiredType);

            if (!hasArtifact)
                throw new InvalidOperationException(
                    $"Cannot advance from {sprint.CurrentStage}: " +
                    $"required artifact '{requiredType}' has not been stored.");
        }

        // P1.4 verdict gate (design §4.4): leaving Implementation requires the
        // most recent SelfEvaluationReport to have OverallVerdict=AllPass.
        // Append-only artifact storage means we order by CreatedAt desc and
        // tie-break by Id desc (same-tick concurrent inserts).
        if (string.Equals(sprint.CurrentStage, "Implementation", StringComparison.Ordinal))
        {
            var latestReport = await _db.SprintArtifacts
                .Where(a => a.SprintId == sprintId
                    && a.Type == nameof(ArtifactType.SelfEvaluationReport))
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Select(a => a.Content)
                .FirstOrDefaultAsync();

            if (latestReport is null)
                throw new InvalidOperationException(
                    "Cannot advance from Implementation: no SelfEvaluationReport stored. " +
                    "Run RUN_SELF_EVAL and STORE_ARTIFACT a SelfEvaluationReport first.");

            SelfEvaluationReport? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<SelfEvaluationReport>(
                    latestReport,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException) { /* fall through to null check */ }

            if (parsed is null)
                throw new InvalidOperationException(
                    "Cannot advance from Implementation: latest SelfEvaluationReport is unparseable.");

            if (parsed.OverallVerdict != SelfEvaluationOverallVerdict.AllPass)
                throw new InvalidOperationException(
                    $"Cannot advance from Implementation: latest SelfEvaluationReport verdict is " +
                    $"'{parsed.OverallVerdict}', not 'AllPass'. Run RUN_SELF_EVAL again to re-evaluate.");
        }

        // Stage prerequisites — skipped when force=true
        if (!force)
        {
            var prereqResult = await CheckPrerequisitesAsync(sprintId, sprint.CurrentStage);
            if (!prereqResult.Passed)
                throw new InvalidOperationException(prereqResult.Message);
        }

        if (_signOffRequiredStages.Contains(sprint.CurrentStage))
        {
            sprint.AwaitingSignOff = true;
            sprint.PendingStage = Stages[currentIndex + 1];
            sprint.SignOffRequestedAt = DateTime.UtcNow;

            QueueEvent(ActivityEventType.SprintStageAdvanced,
                $"Sprint #{sprint.Number} awaiting user sign-off to advance from {sprint.CurrentStage} → {sprint.PendingStage}",
                new Dictionary<string, object?>
                {
                    ["sprintId"] = sprintId,
                    ["action"] = "signoff_requested",
                    ["currentStage"] = sprint.CurrentStage,
                    ["pendingStage"] = sprint.PendingStage,
                    ["awaitingSignOff"] = true,
                });

            await _db.SaveChangesAsync();
            FlushEvents();

            _logger.LogInformation(
                "Sprint #{Number} ({Id}) awaiting sign-off: {Current} → {Pending}",
                sprint.Number, sprint.Id, sprint.CurrentStage, sprint.PendingStage);

            return sprint;
        }

        var previousStage = sprint.CurrentStage;
        sprint.CurrentStage = Stages[currentIndex + 1];
        // P1.2: reset per-stage self-drive counters when stage changes so
        // the new stage starts with a fresh round/continuation budget.
        // Otherwise rounds spent in Planning would count toward the
        // Implementation per-stage cap, causing premature halts.
        sprint.RoundsThisStage = 0;
        sprint.SelfDriveContinuations = 0;

        // P1.4: clear self-eval window state on a successful Implementation→
        // FinalSynthesis advance. LastSelfEvalAt / LastSelfEvalVerdict are
        // audit fields kept across the transition; only the in-flight gate
        // and attempt counter reset.
        if (string.Equals(previousStage, "Implementation", StringComparison.Ordinal))
        {
            sprint.SelfEvaluationInFlight = false;
            sprint.SelfEvalAttempts = 0;
        }

        QueueEvent(ActivityEventType.SprintStageAdvanced,
            $"Sprint #{sprint.Number} advanced: {previousStage} → {sprint.CurrentStage}" + (force ? " (forced)" : ""),
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["action"] = "advanced",
                ["previousStage"] = previousStage,
                ["currentStage"] = sprint.CurrentStage,
                ["awaitingSignOff"] = false,
                ["forced"] = force,
            });

        // Snapshot active rooms BEFORE sync — at FinalSynthesis the sync flips
        // matched rooms to Status=Completed, which would otherwise hide them
        // from the announcer's default "Status != Completed" workspace query.
        var targetRoomIds = await CaptureActiveRoomIdsAsync(sprint.WorkspacePath);

        await SyncWorkspaceRoomsToStageAsync(sprint.WorkspacePath, sprintId, sprint.CurrentStage);

        await _db.SaveChangesAsync();
        FlushEvents();

        _logger.LogInformation(
            "Advanced sprint #{Number} ({Id}) from {Previous} → {Current}",
            sprint.Number, sprint.Id, previousStage, sprint.CurrentStage);

        // P1.3: announce the transition + wake the orchestrator so an agent
        // round fires reflecting the new stage's intent. Best-effort —
        // failures inside the announcer are logged and swallowed there.
        if (_announcer is not null)
        {
            await _announcer.AnnounceAsync(
                sprint, previousStage, trigger: force ? "forced" : null, targetRoomIds: targetRoomIds);
        }

        return sprint;
    }

    /// <summary>
    /// Approves a pending stage advancement (user sign-off).
    /// Only valid when the sprint is AwaitingSignOff.
    /// </summary>
    public async Task<SprintEntity> ApproveAdvanceAsync(string sprintId)
    {
        var sprint = await _db.Sprints.FindAsync(sprintId)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");

        if (!sprint.AwaitingSignOff || sprint.PendingStage is null)
            throw new InvalidOperationException(
                $"Sprint {sprintId} is not awaiting sign-off.");

        var previousStage = sprint.CurrentStage;
        sprint.CurrentStage = sprint.PendingStage;
        sprint.AwaitingSignOff = false;
        sprint.PendingStage = null;
        sprint.SignOffRequestedAt = null;
        // P1.2: reset per-stage self-drive counters when stage changes
        // (mirrors the AdvanceStageAsync path — see rationale there).
        sprint.RoundsThisStage = 0;
        sprint.SelfDriveContinuations = 0;

        QueueEvent(ActivityEventType.SprintStageAdvanced,
            $"Sprint #{sprint.Number} advanced (user approved): {previousStage} → {sprint.CurrentStage}",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["action"] = "approved",
                ["previousStage"] = previousStage,
                ["currentStage"] = sprint.CurrentStage,
                ["awaitingSignOff"] = false,
            });

        // Snapshot before sync — see comment in AdvanceStageAsync.
        var targetRoomIds = await CaptureActiveRoomIdsAsync(sprint.WorkspacePath);

        await SyncWorkspaceRoomsToStageAsync(sprint.WorkspacePath, sprintId, sprint.CurrentStage);

        await _db.SaveChangesAsync();
        FlushEvents();

        _logger.LogInformation(
            "User approved sprint #{Number} ({Id}) advance: {Previous} → {Current}",
            sprint.Number, sprint.Id, previousStage, sprint.CurrentStage);

        // P1.3: announce the transition + wake the orchestrator. Best-effort.
        if (_announcer is not null)
        {
            await _announcer.AnnounceAsync(
                sprint, previousStage, trigger: "approved", targetRoomIds: targetRoomIds);
        }

        return sprint;
    }

    /// <summary>
    /// Rejects a pending stage advancement. Clears AwaitingSignOff so
    /// agents can revise their work and request advancement again.
    /// </summary>
    public async Task<SprintEntity> RejectAdvanceAsync(string sprintId)
    {
        var sprint = await _db.Sprints.FindAsync(sprintId)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");

        if (!sprint.AwaitingSignOff)
            throw new InvalidOperationException(
                $"Sprint {sprintId} is not awaiting sign-off.");

        var pendingStage = sprint.PendingStage;
        sprint.AwaitingSignOff = false;
        sprint.PendingStage = null;
        sprint.SignOffRequestedAt = null;

        QueueEvent(ActivityEventType.SprintStageAdvanced,
            $"Sprint #{sprint.Number} advance rejected by user — staying at {sprint.CurrentStage}",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["action"] = "rejected",
                ["currentStage"] = sprint.CurrentStage,
                ["awaitingSignOff"] = false,
            });

        await _db.SaveChangesAsync();
        FlushEvents();

        _logger.LogInformation(
            "User rejected sprint #{Number} ({Id}) advance from {Current} → {Pending}",
            sprint.Number, sprint.Id, sprint.CurrentStage, pendingStage);

        return sprint;
    }

    /// <summary>
    /// Auto-rejects a sign-off that has timed out. Clears AwaitingSignOff
    /// and emits an event with reason "timeout".
    /// </summary>
    public async Task<SprintEntity> TimeOutSignOffAsync(string sprintId, CancellationToken ct = default)
    {
        var sprint = await _db.Sprints.FindAsync([sprintId], ct)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");

        if (!sprint.AwaitingSignOff)
            throw new InvalidOperationException(
                $"Sprint {sprintId} is not awaiting sign-off.");

        var pendingStage = sprint.PendingStage;
        sprint.AwaitingSignOff = false;
        sprint.PendingStage = null;
        sprint.SignOffRequestedAt = null;

        QueueEvent(ActivityEventType.SprintStageAdvanced,
            $"Sprint #{sprint.Number} sign-off timed out — auto-rejected, staying at {sprint.CurrentStage}",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["action"] = "timeout_rejected",
                ["currentStage"] = sprint.CurrentStage,
                ["pendingStage"] = pendingStage,
                ["awaitingSignOff"] = false,
                ["reason"] = "timeout",
            });

        await _db.SaveChangesAsync(ct);
        FlushEvents();

        _logger.LogWarning(
            "Sprint #{Number} ({Id}) sign-off timed out — auto-rejected from {Current} → {Pending}",
            sprint.Number, sprint.Id, sprint.CurrentStage, pendingStage);

        return sprint;
    }

    // ── Static Helpers ───────────────────────────────────────────

    /// <summary>Returns the index of the given stage, or -1 if not found.</summary>
    public static int GetStageIndex(string stage) => Stages.IndexOf(stage);

    /// <summary>Returns the next stage after the given one, or null if it's the last.</summary>
    public static string? GetNextStage(string stage)
    {
        var idx = Stages.IndexOf(stage);
        return idx >= 0 && idx < Stages.Count - 1 ? Stages[idx + 1] : null;
    }

    // ── Stage Prerequisites ─────────────────────────────────────

    /// <summary>
    /// Result of a stage prerequisite check. Failed results include a
    /// human-readable message and up to <see cref="MaxBlockerDetails"/>
    /// blocking item descriptions.
    /// </summary>
    internal record PrerequisiteResult(bool Passed, string Message, List<string>? BlockingDetails = null)
    {
        internal static readonly PrerequisiteResult Ok = new(true, string.Empty);
    }

    private const int MaxBlockerDetails = 10;

    /// <summary>
    /// Checks all prerequisites for leaving the given stage. Returns
    /// <see cref="PrerequisiteResult.Ok"/> if no prerequisites are
    /// registered or all pass.
    /// </summary>
    private async Task<PrerequisiteResult> CheckPrerequisitesAsync(string sprintId, string currentStage)
    {
        return currentStage switch
        {
            "Implementation" => await CheckImplementationPrerequisitesAsync(sprintId),
            _ => PrerequisiteResult.Ok,
        };
    }

    /// <summary>
    /// Implementation stage prerequisite: all tasks linked to this sprint
    /// must be in a terminal status (Completed or Cancelled).
    /// </summary>
    private async Task<PrerequisiteResult> CheckImplementationPrerequisitesAsync(string sprintId)
    {
        var terminalStatuses = RoomLifecycleService.TerminalTaskStatuses;

        var incompleteTasks = await _db.Tasks
            .Where(t => t.SprintId == sprintId && !terminalStatuses.Contains(t.Status))
            .Select(t => new { t.Id, t.Title, t.Status })
            .Take(MaxBlockerDetails + 1)
            .ToListAsync();

        if (incompleteTasks.Count == 0)
            return PrerequisiteResult.Ok;

        var totalIncomplete = incompleteTasks.Count > MaxBlockerDetails
            ? $"{MaxBlockerDetails}+" : incompleteTasks.Count.ToString();

        var details = incompleteTasks
            .Take(MaxBlockerDetails)
            .Select(t => $"{t.Title} ({t.Status})")
            .ToList();

        return new PrerequisiteResult(
            Passed: false,
            Message: $"Cannot advance from Implementation: {totalIncomplete} task(s) are not completed or cancelled. "
                + "Complete or cancel all tasks first, or use force=true to override.",
            BlockingDetails: details);
    }

    // ── Room Phase Sync ─────────────────────────────────────────

    /// <summary>
    /// Captures the IDs of rooms eligible to receive the stage-advance
    /// announcement before <see cref="SyncWorkspaceRoomsToStageAsync"/>
    /// potentially flips them to <c>Completed</c> (which happens at
    /// <c>Implementation → FinalSynthesis</c>). Returns rooms that are
    /// currently neither archived nor completed in the workspace.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> CaptureActiveRoomIdsAsync(string workspacePath)
    {
        if (string.IsNullOrEmpty(workspacePath))
            return Array.Empty<string>();

        var archived = nameof(RoomStatus.Archived);
        var completed = nameof(RoomStatus.Completed);

        return await _db.Rooms
            .Where(r => r.WorkspacePath == workspacePath
                && r.Status != archived
                && r.Status != completed)
            .Select(r => r.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Mirrors the sprint's new stage to every room in the same workspace whose
    /// phase differs. Keeps the presentation-layer filters (RoomSnapshotBuilder,
    /// conversation round selection) from diverging when agents drive the sprint
    /// stage machine via ADVANCE_STAGE. See <see cref="Services.RoomService.TransitionPhaseAsync"/>
    /// for the human-driven per-room override path; this sync intentionally
    /// bypasses phase prerequisite validation because the sprint is the
    /// authoritative driver once it has advanced. Emits a <c>PhaseChanged</c>
    /// activity event per updated room for observability.
    /// </summary>
    private async Task SyncWorkspaceRoomsToStageAsync(string workspacePath, string sprintId, string newStage)
    {
        if (string.IsNullOrEmpty(workspacePath))
            return;

        // Skip rooms the sync must not revive:
        //   - Archived: terminal historical state; sync would silently unarchive them.
        //   - Completed: already wrapped up (possibly by a prior sprint in this workspace);
        //     advancing a new sprint's stage should not reactivate old rooms.
        // This also preserves the invariant that Status=Completed implies phase=FinalSynthesis
        // by refusing to move a Completed room back to a non-terminal phase.
        var archivedStatus = nameof(RoomStatus.Archived);
        var completedStatus = nameof(RoomStatus.Completed);

        var rooms = await _db.Rooms
            .Where(r => r.WorkspacePath == workspacePath
                && r.CurrentPhase != newStage
                && r.Status != archivedStatus
                && r.Status != completedStatus)
            .ToListAsync();

        if (rooms.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var isTerminal = string.Equals(newStage, nameof(CollaborationPhase.FinalSynthesis), StringComparison.Ordinal);

        // Resolve strict main-room exemption set once for this workspace (B1).
        // Falls back to the name-suffix predicate when IRoomLifecycleService
        // isn't wired (legacy test setups). Production DI provides it.
        HashSet<string>? exemptIds = null;
        if (isTerminal && _lifecycle is not null)
            exemptIds = await _lifecycle.GetExemptMainRoomIdsAsync(workspacePath);

        foreach (var room in rooms)
        {
            var oldPhase = room.CurrentPhase;
            room.CurrentPhase = newStage;
            room.UpdatedAt = now;
            if (isTerminal)
            {
                bool isPersistentMain = exemptIds is not null
                    ? exemptIds.Contains(room.Id)
                    : RoomLifecycleService.IsMainCollaborationRoomName(room.Name);
                if (!isPersistentMain)
                    room.Status = completedStatus;
            }

            QueueEvent(ActivityEventType.PhaseChanged,
                $"Room '{room.Name}' phase synced to sprint stage: {oldPhase} → {newStage}",
                new Dictionary<string, object?>
                {
                    ["roomId"] = room.Id,
                    ["previousPhase"] = oldPhase,
                    ["currentPhase"] = newStage,
                    ["source"] = "sprint-sync",
                    ["sprintId"] = sprintId,
                },
                roomId: room.Id);
        }

        _logger.LogInformation(
            "Synced {Count} room(s) in workspace '{Workspace}' to sprint stage '{Stage}'",
            rooms.Count, workspacePath, newStage);
    }

    // ── Event Publishing ────────────────────────────────────────

    private readonly List<ActivityEvent> _pendingEvents = [];

    private void QueueEvent(
        ActivityEventType type, string message,
        Dictionary<string, object?>? metadata = null,
        string? roomId = null)
    {
        var evt = new ActivityEvent(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            Severity: ActivitySeverity.Info,
            RoomId: roomId,
            ActorId: null,
            TaskId: null,
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
