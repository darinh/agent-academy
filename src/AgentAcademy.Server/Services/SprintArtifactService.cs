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
public sealed class SprintArtifactService : ISprintArtifactService
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
        if (!SprintStageService.Stages.Contains(stage))
            throw new ArgumentException(
                $"Invalid stage '{stage}'. Valid stages: {string.Join(", ", SprintStageService.Stages)}",
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

                case ArtifactType.SelfEvaluationReport:
                    ValidateSelfEvaluationReport(content, options);
                    break;
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"Invalid JSON for artifact type '{type}': {ex.Message}", nameof(content), ex);
        }
    }

    /// <summary>
    /// Static (parse-level) validation for <see cref="ArtifactType.SelfEvaluationReport"/>
    /// content. Enforces the rules from
    /// <c>specs/100-product-vision/p1-4-self-evaluation-design.md §3.2</c> that
    /// can be checked without a database lookup:
    /// <list type="bullet">
    /// <item>Required scalars (<c>Attempt &gt;= 1</c>) and <c>Items</c> non-null/non-empty.</item>
    /// <item>Per-item required fields (<c>TaskId</c>, <c>SuccessCriteria</c>, <c>Evidence</c>).</item>
    /// <item>No duplicate <c>TaskId</c>s.</item>
    /// <item>Every non-PASS item has a non-empty <c>FixPlan</c>.</item>
    /// <item><c>OverallVerdict</c> matches the per-item rollup
    ///     (<c>AllPass</c> iff every item PASS; else <c>AnyFail</c> if any FAIL;
    ///     else <c>Unverified</c>).</item>
    /// </list>
    /// DB-aware checks (item TaskIds match the sprint's non-cancelled task set,
    /// SuccessCriteria copied verbatim from each TaskEntity) are NOT enforced
    /// here — they live in the verdict path added by the next P1.4 PR, which
    /// already needs the sprint context to compute the verdict.
    /// </summary>
    private static void ValidateSelfEvaluationReport(string content, JsonSerializerOptions options)
    {
        // Two-stage parse: first verify required JSON properties are PRESENT (not
        // just successfully deserialized). System.Text.Json maps a missing enum
        // field to numeric 0 (= PASS / AllPass here), which would let an agent
        // silently submit a report with no Verdict and have it accepted as PASS.
        // Catching presence at the JsonDocument level before model deserialization
        // closes that loophole.
        //
        // Property lookups are case-INSENSITIVE to match the deserializer's
        // PropertyNameCaseInsensitive=true contract (otherwise camelCase payloads
        // that previously round-tripped would now be rejected as missing).
        using (var doc = JsonDocument.Parse(content))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException(
                    "SelfEvaluationReport content must be a JSON object.");

            RequireJsonProperty(doc.RootElement, "Attempt");
            RequireJsonProperty(doc.RootElement, "Items");
            RequireJsonProperty(doc.RootElement, "OverallVerdict");

            if (TryGetPropertyIgnoreCase(doc.RootElement, "Items", out var itemsElement)
                && itemsElement.ValueKind == JsonValueKind.Array)
            {
                var idx = 0;
                foreach (var rawItem in itemsElement.EnumerateArray())
                {
                    if (rawItem.ValueKind == JsonValueKind.Null)
                        throw new ArgumentException(
                            $"Items[{idx}] must be a JSON object, got null.");
                    if (rawItem.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException(
                            $"Items[{idx}] must be a JSON object, got {rawItem.ValueKind}.");

                    RequireJsonProperty(rawItem, "TaskId", $"Items[{idx}].TaskId");
                    RequireJsonProperty(rawItem, "SuccessCriteria", $"Items[{idx}].SuccessCriteria");
                    RequireJsonProperty(rawItem, "Verdict", $"Items[{idx}].Verdict");
                    RequireJsonProperty(rawItem, "Evidence", $"Items[{idx}].Evidence");
                    idx++;
                }
            }
        }

        var report = JsonSerializer.Deserialize<SelfEvaluationReport>(content, options)
            ?? throw new ArgumentException("Content deserialized to null.");

        if (report.Attempt < 1)
            throw new ArgumentException(
                $"Required field 'Attempt' must be >= 1 (got {report.Attempt}).");

        RequireList(report.Items, "Items");
        if (report.Items.Count == 0)
            throw new ArgumentException(
                "Required field 'Items' must contain at least one entry — " +
                "a sprint with zero non-cancelled tasks cannot be self-evaluated.");

        var seenTaskIds = new HashSet<string>(StringComparer.Ordinal);
        var anyFail = false;
        var anyUnverified = false;
        var allPass = true;

        for (var i = 0; i < report.Items.Count; i++)
        {
            var item = report.Items[i];
            // Defensive: STJ usually treats `null` array elements as null in the
            // List<T> too. We've already screened `Items: [null]` at the
            // JsonDocument layer, but keep this guard so any future deserialization
            // path can't reach the field accesses below with a null item.
            if (item is null)
                throw new ArgumentException(
                    $"Items[{i}] must be a non-null entry.");

            RequireString(item.TaskId, $"Items[{i}].TaskId");
            RequireString(item.SuccessCriteria, $"Items[{i}].SuccessCriteria");
            RequireString(item.Evidence, $"Items[{i}].Evidence");

            if (!seenTaskIds.Add(item.TaskId))
                throw new ArgumentException(
                    $"Duplicate TaskId '{item.TaskId}' in Items — each non-cancelled task " +
                    "must appear exactly once.");

            switch (item.Verdict)
            {
                case SelfEvaluationVerdict.PASS:
                    break;
                case SelfEvaluationVerdict.FAIL:
                    anyFail = true;
                    allPass = false;
                    if (string.IsNullOrWhiteSpace(item.FixPlan))
                        throw new ArgumentException(
                            $"Items[{i}].FixPlan is required when Verdict is FAIL.");
                    break;
                case SelfEvaluationVerdict.UNVERIFIED:
                    anyUnverified = true;
                    allPass = false;
                    if (string.IsNullOrWhiteSpace(item.FixPlan))
                        throw new ArgumentException(
                            $"Items[{i}].FixPlan is required when Verdict is UNVERIFIED.");
                    break;
                default:
                    throw new ArgumentException(
                        $"Items[{i}].Verdict has unknown value '{item.Verdict}'.");
            }
        }

        var expected = allPass
            ? SelfEvaluationOverallVerdict.AllPass
            : (anyFail
                ? SelfEvaluationOverallVerdict.AnyFail
                : SelfEvaluationOverallVerdict.Unverified);

        // Defensive: anyUnverified is consumed by the rollup-without-FAIL branch.
        // Reference it so analyzers don't flag it as unused if logic is later refactored.
        _ = anyUnverified;

        if (report.OverallVerdict != expected)
            throw new ArgumentException(
                $"OverallVerdict mismatch: rollup of Items expects '{expected}' but report " +
                $"declares '{report.OverallVerdict}'. The agent cannot disagree with the " +
                "computed rollup.");
    }

    private static void RequireJsonProperty(
        JsonElement element, string propertyName, string? displayName = null)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value)
            || value.ValueKind == JsonValueKind.Undefined
            || value.ValueKind == JsonValueKind.Null)
        {
            throw new ArgumentException(
                $"Required field '{displayName ?? propertyName}' is missing or null.");
        }
    }

    /// <summary>
    /// Case-insensitive variant of <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>.
    /// Matches the deserializer's <c>PropertyNameCaseInsensitive = true</c> contract so
    /// that camelCase payloads (e.g. <c>attempt</c>, <c>items</c>, <c>overallVerdict</c>)
    /// that round-trip through deserialization also pass the presence pre-check.
    /// </summary>
    private static bool TryGetPropertyIgnoreCase(
        JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        // Fast path: exact case match (the canonical PascalCase the agents are
        // instructed to emit). Avoids enumerating properties on the hot path.
        if (element.TryGetProperty(propertyName, out value))
            return true;

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
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
