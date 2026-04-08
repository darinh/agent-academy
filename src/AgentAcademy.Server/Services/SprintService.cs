using System.Collections.ObjectModel;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages sprint lifecycle: creation, stage advancement, artifact storage,
/// and completion. Each workspace has at most one active sprint.
/// </summary>
public sealed class SprintService
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
    private static readonly IReadOnlyDictionary<string, string> RequiredArtifactByStage =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Intake"] = "RequirementsDocument",
            ["Planning"] = "SprintPlan",
            ["Validation"] = "ValidationReport",
            ["FinalSynthesis"] = "SprintReport",
        };

    private readonly AgentAcademyDbContext _db;
    private readonly ActivityBroadcaster _activityBus;
    private readonly ILogger<SprintService> _logger;

    public SprintService(AgentAcademyDbContext db, ActivityBroadcaster activityBus, ILogger<SprintService> logger)
    {
        _db = db;
        _activityBus = activityBus;
        _logger = logger;
    }

    // ── Create ───────────────────────────────────────────────────

    /// <summary>
    /// Creates the next sprint for a workspace. If a previous sprint exists and
    /// has overflow artifacts, they are linked via <see cref="SprintEntity.OverflowFromSprintId"/>.
    /// Throws if there is already an active sprint for this workspace.
    /// </summary>
    public async Task<SprintEntity> CreateSprintAsync(string workspacePath)
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
                .FirstOrDefaultAsync(a => a.SprintId == lastSprint.Id && a.Type == "OverflowRequirements");
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

        Publish(ActivityEventType.SprintStarted, null, null, null,
            $"Sprint #{sprint.Number} started for workspace {workspacePath}");

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Created sprint #{Number} ({Id}) for workspace {Workspace}{Overflow}",
            sprint.Number, sprint.Id, workspacePath,
            overflowFrom is not null ? $" (overflow from {overflowFrom})" : "");

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

    // ── Artifacts ────────────────────────────────────────────────

    /// <summary>
    /// Stores a deliverable artifact for a sprint stage.
    /// If an artifact of the same type already exists for the stage, it is updated.
    /// </summary>
    public async Task<SprintArtifactEntity> StoreArtifactAsync(
        string sprintId, string stage, string type, string content, string? agentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sprintId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var sprint = await _db.Sprints.FindAsync(sprintId)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");

        if (sprint.Status != "Active")
            throw new InvalidOperationException(
                $"Cannot store artifacts in sprint {sprintId} — status is {sprint.Status}.");

        ValidateStage(stage);

        var existing = await _db.SprintArtifacts
            .Where(a => a.SprintId == sprintId && a.Stage == stage && a.Type == type)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            existing.Content = content;
            existing.UpdatedAt = DateTime.UtcNow;

            Publish(ActivityEventType.SprintArtifactStored, null, agentId, null,
                $"Artifact '{type}' updated for sprint stage {stage}");

            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Updated artifact {Type} for sprint {SprintId} stage {Stage}",
                type, sprintId, stage);

            return existing;
        }

        var artifact = new SprintArtifactEntity
        {
            SprintId = sprintId,
            Stage = stage,
            Type = type,
            Content = content,
            CreatedByAgentId = agentId,
            CreatedAt = DateTime.UtcNow,
        };

        _db.SprintArtifacts.Add(artifact);

        Publish(ActivityEventType.SprintArtifactStored, null, agentId, null,
            $"Artifact '{type}' stored for sprint stage {stage}");

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Stored artifact {Type} for sprint {SprintId} stage {Stage} by {Agent}",
            type, sprintId, stage, agentId ?? "(system)");

        return artifact;
    }

    /// <summary>
    /// Returns artifacts for a sprint, optionally filtered by stage.
    /// </summary>
    public async Task<List<SprintArtifactEntity>> GetSprintArtifactsAsync(
        string sprintId, string? stage = null)
    {
        var query = _db.SprintArtifacts
            .Where(a => a.SprintId == sprintId);

        if (!string.IsNullOrEmpty(stage))
        {
            ValidateStage(stage);
            query = query.Where(a => a.Stage == stage);
        }

        return await query
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
    }

    // ── Stage Advancement ────────────────────────────────────────

    /// <summary>
    /// Advances the sprint to the next stage. Validates that any required
    /// artifact for the current stage exists before allowing advancement.
    /// Returns the updated sprint.
    /// </summary>
    public async Task<SprintEntity> AdvanceStageAsync(string sprintId)
    {
        var sprint = await _db.Sprints.FindAsync(sprintId)
            ?? throw new InvalidOperationException($"Sprint {sprintId} not found.");

        if (sprint.Status != "Active")
            throw new InvalidOperationException(
                $"Cannot advance sprint {sprintId} — status is {sprint.Status}.");

        var currentIndex = Stages.IndexOf(sprint.CurrentStage);
        if (currentIndex < 0)
            throw new InvalidOperationException(
                $"Sprint {sprintId} is in unknown stage '{sprint.CurrentStage}'.");

        if (currentIndex >= Stages.Count - 1)
            throw new InvalidOperationException(
                $"Sprint {sprintId} is already at the final stage ({sprint.CurrentStage}). " +
                "Use CompleteSprintAsync to finish it.");

        // Check for required artifact before advancing
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

        var previousStage = sprint.CurrentStage;
        sprint.CurrentStage = Stages[currentIndex + 1];

        Publish(ActivityEventType.SprintStageAdvanced, null, null, null,
            $"Sprint #{sprint.Number} advanced: {previousStage} → {sprint.CurrentStage}");

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Advanced sprint #{Number} ({Id}) from {Previous} → {Current}",
            sprint.Number, sprint.Id, previousStage, sprint.CurrentStage);

        return sprint;
    }

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
        if (!force && RequiredArtifactByStage.TryGetValue("FinalSynthesis", out var requiredType))
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

        Publish(ActivityEventType.SprintCompleted, null, null, null,
            $"Sprint #{sprint.Number} completed for workspace {sprint.WorkspacePath}");

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Completed sprint #{Number} ({Id}) for workspace {Workspace}",
            sprint.Number, sprint.Id, sprint.WorkspacePath);

        return sprint;
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

        Publish(ActivityEventType.SprintCompleted, null, null, null,
            $"Sprint #{sprint.Number} cancelled for workspace {sprint.WorkspacePath}");

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Cancelled sprint #{Number} ({Id}) for workspace {Workspace}",
            sprint.Number, sprint.Id, sprint.WorkspacePath);

        return sprint;
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static void ValidateStage(string stage)
    {
        if (!Stages.Contains(stage))
            throw new ArgumentException(
                $"Invalid stage '{stage}'. Valid stages: {string.Join(", ", Stages)}",
                nameof(stage));
    }

    /// <summary>
    /// Returns the index of the given stage, or -1 if not found.
    /// </summary>
    public static int GetStageIndex(string stage) => Stages.IndexOf(stage);

    /// <summary>
    /// Returns the next stage after the given one, or null if it's the last.
    /// </summary>
    public static string? GetNextStage(string stage)
    {
        var idx = Stages.IndexOf(stage);
        return idx >= 0 && idx < Stages.Count - 1 ? Stages[idx + 1] : null;
    }

    private void Publish(
        ActivityEventType type, string? roomId, string? actorId, string? taskId, string message)
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
            OccurredAt: DateTime.UtcNow
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
        });

        _activityBus.Broadcast(evt);
    }
}
