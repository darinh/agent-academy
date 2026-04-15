using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Manages sprint artifact storage, retrieval, and validation.
/// Extracted from SprintService to separate artifact concerns from sprint lifecycle.
/// </summary>
public sealed class SprintArtifactService
{
    private readonly AgentAcademyDbContext _db;
    private readonly IActivityBroadcaster _activityBus;
    private readonly ILogger<SprintArtifactService> _logger;

    public SprintArtifactService(
        AgentAcademyDbContext db,
        IActivityBroadcaster activityBus,
        ILogger<SprintArtifactService> logger)
    {
        _db = db;
        _activityBus = activityBus;
        _logger = logger;
    }

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
        ValidateArtifactContent(type, content);

        var existing = await _db.SprintArtifacts
            .Where(a => a.SprintId == sprintId && a.Stage == stage && a.Type == type)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            existing.Content = content;
            existing.UpdatedAt = DateTime.UtcNow;

            var updateEvt = EmitEvent(ActivityEventType.SprintArtifactStored, agentId,
                $"Artifact '{type}' updated for sprint stage {stage}",
                new Dictionary<string, object?>
                {
                    ["sprintId"] = sprintId,
                    ["artifactId"] = existing.Id,
                    ["stage"] = stage,
                    ["artifactType"] = type,
                    ["createdByAgentId"] = agentId,
                    ["isUpdate"] = true,
                });

            await _db.SaveChangesAsync();
            _activityBus.Broadcast(updateEvt);

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

        var createEvt = EmitEvent(ActivityEventType.SprintArtifactStored, agentId,
            $"Artifact '{type}' stored for sprint stage {stage}",
            new Dictionary<string, object?>
            {
                ["sprintId"] = sprintId,
                ["stage"] = stage,
                ["artifactType"] = type,
                ["createdByAgentId"] = agentId,
                ["isUpdate"] = false,
            });

        await _db.SaveChangesAsync();
        _activityBus.Broadcast(createEvt);

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

    // ── Validation ──────────────────────────────────────────────

    private static void ValidateStage(string stage)
    {
        if (!SprintService.Stages.Contains(stage))
            throw new ArgumentException(
                $"Invalid stage '{stage}'. Valid stages: {string.Join(", ", SprintService.Stages)}",
                nameof(stage));
    }

    /// <summary>
    /// Validates artifact content against the expected schema for known artifact types.
    /// Throws <see cref="ArgumentException"/> if the content is malformed JSON or
    /// missing required fields.
    /// </summary>
    internal static void ValidateArtifactContent(string type, string content)
    {
        if (!Enum.TryParse<ArtifactType>(type, ignoreCase: false, out var artifactType)
            || !Enum.IsDefined(artifactType))
            throw new ArgumentException(
                $"Unknown artifact type '{type}'. Valid types: {string.Join(", ", Enum.GetNames<ArtifactType>())}",
                nameof(type));

        // OverflowRequirements is free-form — no schema to validate
        if (artifactType == ArtifactType.OverflowRequirements)
            return;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        try
        {
            switch (artifactType)
            {
                case ArtifactType.RequirementsDocument:
                    var req = JsonSerializer.Deserialize<RequirementsDocument>(content, options)
                        ?? throw new ArgumentException("Content deserialized to null.");
                    RequireString(req.Title, "Title");
                    RequireString(req.Description, "Description");
                    RequireList(req.InScope, "InScope");
                    RequireList(req.OutOfScope, "OutOfScope");
                    RequireList(req.AcceptanceCriteria, "AcceptanceCriteria");
                    break;

                case ArtifactType.SprintPlan:
                    var plan = JsonSerializer.Deserialize<SprintPlanDocument>(content, options)
                        ?? throw new ArgumentException("Content deserialized to null.");
                    RequireString(plan.Summary, "Summary");
                    RequireList(plan.Phases, "Phases");
                    for (var i = 0; i < plan.Phases.Count; i++)
                    {
                        var phase = plan.Phases[i];
                        RequireString(phase.Name, $"Phases[{i}].Name");
                        RequireString(phase.Description, $"Phases[{i}].Description");
                        RequireList(phase.Deliverables, $"Phases[{i}].Deliverables");
                    }
                    break;

                case ArtifactType.ValidationReport:
                    var vr = JsonSerializer.Deserialize<ValidationReport>(content, options)
                        ?? throw new ArgumentException("Content deserialized to null.");
                    RequireString(vr.Verdict, "Verdict");
                    RequireList(vr.Findings, "Findings");
                    break;

                case ArtifactType.SprintReport:
                    var sr = JsonSerializer.Deserialize<SprintReport>(content, options)
                        ?? throw new ArgumentException("Content deserialized to null.");
                    RequireString(sr.Summary, "Summary");
                    RequireList(sr.Delivered, "Delivered");
                    RequireList(sr.Learnings, "Learnings");
                    break;
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"Invalid JSON for artifact type '{type}': {ex.Message}", nameof(content), ex);
        }
    }

    private static void RequireString(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                $"Required field '{fieldName}' is missing or empty.");
    }

    private static void RequireList<T>(List<T>? value, string fieldName)
    {
        if (value is null)
            throw new ArgumentException(
                $"Required field '{fieldName}' is missing.");
    }

    // ── Event Helper ────────────────────────────────────────────

    private ActivityEvent EmitEvent(
        ActivityEventType type, string? actorId, string message,
        Dictionary<string, object?>? metadata = null)
    {
        var evt = new ActivityEvent(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            Severity: ActivitySeverity.Info,
            RoomId: null,
            ActorId: actorId,
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

        return evt;
    }
}
