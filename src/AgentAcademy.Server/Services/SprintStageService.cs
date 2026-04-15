using System.Collections.ObjectModel;
using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.EntityFrameworkCore;

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
            ["FinalSynthesis"] = "SprintReport",
        };

    /// <summary>
    /// Stages that require user sign-off before advancing.
    /// When an agent triggers ADVANCE_STAGE from one of these stages,
    /// the sprint enters AwaitingSignOff state until a human approves.
    /// </summary>
    private static readonly IReadOnlySet<string> SignOffRequiredStages =
        new HashSet<string>(StringComparer.Ordinal) { "Intake", "Planning" };

    private readonly AgentAcademyDbContext _db;
    private readonly IActivityBroadcaster _activityBus;
    private readonly ILogger<SprintStageService> _logger;

    public SprintStageService(AgentAcademyDbContext db, IActivityBroadcaster activityBus, ILogger<SprintStageService> logger)
    {
        _db = db;
        _activityBus = activityBus;
        _logger = logger;
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

        // Stage prerequisites — skipped when force=true
        if (!force)
        {
            var prereqResult = await CheckPrerequisitesAsync(sprintId, sprint.CurrentStage);
            if (!prereqResult.Passed)
                throw new InvalidOperationException(prereqResult.Message);
        }

        if (SignOffRequiredStages.Contains(sprint.CurrentStage))
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

        await _db.SaveChangesAsync();
        FlushEvents();

        _logger.LogInformation(
            "Advanced sprint #{Number} ({Id}) from {Previous} → {Current}",
            sprint.Number, sprint.Id, previousStage, sprint.CurrentStage);

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

        await _db.SaveChangesAsync();
        FlushEvents();

        _logger.LogInformation(
            "User approved sprint #{Number} ({Id}) advance: {Previous} → {Current}",
            sprint.Number, sprint.Id, previousStage, sprint.CurrentStage);

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

    // ── Event Publishing ────────────────────────────────────────

    private readonly List<ActivityEvent> _pendingEvents = [];

    private void QueueEvent(
        ActivityEventType type, string message,
        Dictionary<string, object?>? metadata = null)
    {
        var evt = new ActivityEvent(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            Severity: ActivitySeverity.Info,
            RoomId: null,
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
