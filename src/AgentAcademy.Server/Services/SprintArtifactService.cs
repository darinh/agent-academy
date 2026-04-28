using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly SelfEvalOptions _selfEvalOptions;

    public SprintArtifactService(
        AgentAcademyDbContext db,
        IActivityBroadcaster activityBus,
        ILogger<SprintArtifactService> logger,
        IOptions<SelfEvalOptions>? selfEvalOptions = null)
    {
        _db = db;
        _activityBus = activityBus;
        _logger = logger;
        _selfEvalOptions = selfEvalOptions?.Value ?? new SelfEvalOptions();
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

        // P1.4 self-eval verdict path: SelfEvaluationReport is append-only,
        // gated on RUN_SELF_EVAL having flipped SelfEvaluationInFlight=true,
        // and atomically updates sprint counters / verdict / blocked state in
        // a single transaction. Lives in its own method so the legacy upsert
        // path below stays untouched for every other artifact type.
        if (string.Equals(type, nameof(ArtifactType.SelfEvaluationReport), StringComparison.Ordinal))
        {
            return await StoreSelfEvaluationReportAsync(sprint, stage, content, agentId);
        }

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

    public async Task<SprintArtifactEntity?> GetLatestSelfEvalReportAsync(string sprintId)
    {
        var reportType = nameof(ArtifactType.SelfEvaluationReport);
        return await _db.SprintArtifacts
            .Where(a => a.SprintId == sprintId && a.Type == reportType)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Returns the <c>OverallVerdict</c> of the most recent
    /// <c>SelfEvaluationReport</c> artifact for a sprint, or <c>null</c> if
    /// none stored / unparseable. Mirrors the query semantics of the
    /// Implementation→FinalSynthesis verdict gate in
    /// <see cref="SprintStageService"/> so the terminal-stage driver and the
    /// gate cannot disagree about which report is "latest". See design §6.1.
    /// </summary>
    public async Task<SelfEvaluationOverallVerdict?> GetLatestSelfEvalVerdictAsync(
        string sprintId, CancellationToken ct = default)
    {
        var reportType = nameof(ArtifactType.SelfEvaluationReport);
        var latestContent = await _db.SprintArtifacts
            .Where(a => a.SprintId == sprintId && a.Type == reportType)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => a.Content)
            .FirstOrDefaultAsync(ct);

        if (latestContent is null) return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<SelfEvaluationReport>(
                latestContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed?.OverallVerdict;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if at least one artifact matching the
    /// (sprint, stage, type) tuple is stored. Used by the terminal-stage
    /// driver to detect <c>SprintReport</c> at FinalSynthesis without
    /// loading content. See design §6.1.
    /// </summary>
    public Task<bool> HasArtifactAsync(
        string sprintId, string stage, string type, CancellationToken ct = default) =>
        _db.SprintArtifacts.AnyAsync(
            a => a.SprintId == sprintId && a.Stage == stage && a.Type == type, ct);

    // ── Self-Evaluation Verdict Path (P1.4) ─────────────────────

    /// <summary>
    /// Stores a <see cref="ArtifactType.SelfEvaluationReport"/> artifact and
    /// processes its verdict. Append-only (every attempt produces a new row).
    /// Single-transaction: artifact insert + sprint counter bump + verdict
    /// state update + auto-block-on-cap + activity events all commit together.
    /// See <c>specs/100-product-vision/p1-4-self-evaluation-design.md §4.3</c>.
    /// </summary>
    private async Task<SprintArtifactEntity> StoreSelfEvaluationReportAsync(
        SprintEntity sprint, string stage, string content, string? agentId)
    {
        // Gate: only at Implementation, only when not blocked, only when
        // RUN_SELF_EVAL has primed the in-flight flag. Without the in-flight
        // gate, STORE_ARTIFACT bypasses the RUN_SELF_EVAL ceremony.
        if (!string.Equals(stage, "Implementation", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"SelfEvaluationReport may only be stored at Implementation stage (got '{stage}').");

        if (!string.Equals(sprint.CurrentStage, "Implementation", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Cannot store SelfEvaluationReport: sprint is in stage '{sprint.CurrentStage}', not Implementation.");

        if (sprint.BlockedAt is not null)
            throw new InvalidOperationException(
                $"Cannot store SelfEvaluationReport: sprint {sprint.Id} is blocked. Unblock first.");

        if (!sprint.SelfEvaluationInFlight)
            throw new InvalidOperationException(
                $"Cannot store SelfEvaluationReport: sprint {sprint.Id} has no self-evaluation in flight. " +
                "Run RUN_SELF_EVAL first to open an evaluation window.");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var report = JsonSerializer.Deserialize<SelfEvaluationReport>(content, options)
            ?? throw new ArgumentException("SelfEvaluationReport content deserialized to null.");

        // DB-aware validation (design §3.2). Static checks (parse-level) ran
        // in ValidateArtifactContent above; these need the sprint context.

        // 1. Items[*].TaskId set ≡ non-cancelled task set for this sprint.
        var terminalCancelled = nameof(Shared.Models.TaskStatus.Cancelled);
        var nonCancelledTasks = await _db.Tasks
            .Where(t => t.SprintId == sprint.Id && t.Status != terminalCancelled)
            .Select(t => new { t.Id, t.SuccessCriteria })
            .ToListAsync();

        var expectedTaskIds = nonCancelledTasks
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
        var actualTaskIds = report.Items
            .Select(i => i.TaskId)
            .ToHashSet(StringComparer.Ordinal);

        if (!expectedTaskIds.SetEquals(actualTaskIds))
        {
            var missing = expectedTaskIds.Except(actualTaskIds).ToList();
            var extra = actualTaskIds.Except(expectedTaskIds).ToList();
            throw new ArgumentException(
                "SelfEvaluationReport TaskId set does not match the sprint's non-cancelled tasks. "
                + (missing.Count > 0 ? $"Missing: [{string.Join(", ", missing)}]. " : "")
                + (extra.Count > 0 ? $"Unexpected: [{string.Join(", ", extra)}]." : ""));
        }

        // 2. Items[*].SuccessCriteria copied verbatim (Ordinal, whitespace-significant).
        var taskCriteria = nonCancelledTasks.ToDictionary(
            t => t.Id, t => t.SuccessCriteria ?? string.Empty, StringComparer.Ordinal);
        for (var i = 0; i < report.Items.Count; i++)
        {
            var item = report.Items[i];
            if (!taskCriteria.TryGetValue(item.TaskId, out var expected))
                continue; // already covered by set-equality above
            if (!string.Equals(item.SuccessCriteria, expected, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Items[{i}].SuccessCriteria does not match TaskEntity.SuccessCriteria for task '{item.TaskId}'. " +
                    "It must be copied verbatim (case-sensitive, whitespace-significant).");
        }

        // 3. Attempt == sprint.SelfEvalAttempts + 1 (idempotence + monotonic).
        var expectedAttempt = sprint.SelfEvalAttempts + 1;
        if (report.Attempt != expectedAttempt)
            throw new ArgumentException(
                $"SelfEvaluationReport.Attempt mismatch: expected {expectedAttempt} " +
                $"(sprint has {sprint.SelfEvalAttempts} prior attempt(s)), got {report.Attempt}. " +
                "Stale or duplicate submissions are rejected.");

        // ── Single transaction: artifact insert + sprint state ──
        var maxAttempts = Math.Max(1, _selfEvalOptions.MaxSelfEvalAttempts);
        var verdictStr = report.OverallVerdict.ToString();
        var now = DateTime.UtcNow;
        var newAttempts = sprint.SelfEvalAttempts + 1;
        var blockedThisRun = false;
        string? blockReason = null;

        // Decision tree (design §4.3):
        //   AllPass    → keep in-flight=true, ADVANCE_STAGE will reset on success.
        //   AnyFail/Unverified, attempts<cap → re-open: in-flight=false (next RUN_SELF_EVAL).
        //   AnyFail/Unverified, attempts==cap → block sprint.
        var keepInFlight = report.OverallVerdict == SelfEvaluationOverallVerdict.AllPass;
        if (!keepInFlight && newAttempts >= maxAttempts)
        {
            blockedThisRun = true;
            blockReason = $"Self-eval failed {newAttempts} times — human input required";
        }

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
        try
        {
            try { tx = await _db.Database.BeginTransactionAsync(); }
            catch (InvalidOperationException) { /* in-memory provider — best-effort atomic */ }

            var artifact = new SprintArtifactEntity
            {
                SprintId = sprint.Id,
                Stage = stage,
                Type = nameof(ArtifactType.SelfEvaluationReport),
                Content = content,
                CreatedByAgentId = agentId,
                CreatedAt = now,
            };
            _db.SprintArtifacts.Add(artifact);

            sprint.SelfEvalAttempts = newAttempts;
            sprint.LastSelfEvalAt = now;
            sprint.LastSelfEvalVerdict = verdictStr;
            sprint.SelfEvaluationInFlight = keepInFlight;
            // Terminal-stage driver coordination (design §6.2):
            // The driver stamps SelfEvalStartedAt when it fires StartedSelfEval.
            // On AllPass, the chain has progressed — clear the marker so the
            // next StartedSelfEval (if any) starts a fresh stall window.
            // On AnyFail/Unverified, leave the marker set; the driver will
            // RE-stamp it on the next StartedSelfEval after the team re-attempts.
            if (keepInFlight)  // AllPass
            {
                sprint.SelfEvalStartedAt = null;
            }
            if (blockedThisRun)
            {
                sprint.BlockedAt = now;
                sprint.BlockReason = blockReason;
            }

            var storedEvt = EmitEvent(ActivityEventType.SprintArtifactStored, agentId,
                $"Artifact 'SelfEvaluationReport' stored for sprint stage {stage} (attempt {newAttempts}, {verdictStr})",
                new Dictionary<string, object?>
                {
                    ["sprintId"] = sprint.Id,
                    ["stage"] = stage,
                    ["artifactType"] = nameof(ArtifactType.SelfEvaluationReport),
                    ["createdByAgentId"] = agentId,
                    ["isUpdate"] = false,
                    ["attempt"] = newAttempts,
                    ["overallVerdict"] = verdictStr,
                });

            var completedEvt = EmitEvent(ActivityEventType.SelfEvalCompleted, agentId,
                $"Self-evaluation attempt {newAttempts} for sprint #{sprint.Number}: {verdictStr}",
                new Dictionary<string, object?>
                {
                    ["sprintId"] = sprint.Id,
                    ["sprintNumber"] = sprint.Number,
                    ["attempt"] = newAttempts,
                    ["overallVerdict"] = verdictStr,
                    ["maxSelfEvalAttempts"] = maxAttempts,
                    ["blocked"] = blockedThisRun,
                });

            ActivityEvent? blockedEvt = null;
            if (blockedThisRun)
            {
                blockedEvt = EmitEvent(ActivityEventType.SprintBlocked, agentId,
                    $"Sprint #{sprint.Number} blocked: {blockReason}",
                    new Dictionary<string, object?>
                    {
                        ["sprintId"] = sprint.Id,
                        ["reason"] = blockReason,
                    });
            }

            await _db.SaveChangesAsync();
            if (tx is not null) await tx.CommitAsync();

            // Post-commit: broadcast queued events. Order: artifact-stored,
            // then verdict, then (optional) blocked, so subscribers see the
            // verdict before any block-driven UI surface flips state.
            _activityBus.Broadcast(storedEvt);
            _activityBus.Broadcast(completedEvt);
            if (blockedEvt is not null)
                _activityBus.Broadcast(blockedEvt);

            _logger.LogInformation(
                "Stored self-evaluation report for sprint #{Number} ({Id}) attempt {Attempt}: {Verdict}{Block}",
                sprint.Number, sprint.Id, newAttempts, verdictStr,
                blockedThisRun ? $" — sprint blocked ({blockReason})" : string.Empty);

            return artifact;
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
                $"Invalid JSON for artifact type '{type}': {ex.Message}{SchemaHintSuffix(artifactType)}",
                nameof(content), ex);
        }
        catch (ArgumentException ex) when (ex.ParamName != "type"
            && !ex.Message.Contains("Expected schema:", StringComparison.Ordinal))
        {
            // Field-level validation (RequireString, RequireList, deserialize-to-null, and
            // SelfEvaluationReport semantic checks) throws plain ArgumentException without
            // the schema. Augment with a compact schema hint so agents can self-correct on
            // the next attempt instead of round-burning to guess the shape.
            throw new ArgumentException(
                $"{ex.Message}{SchemaHintSuffix(artifactType)}",
                ex.ParamName,
                ex);
        }
    }

    /// <summary>
    /// Returns a compact JSON schema hint for a known artifact type, formatted as
    /// a suffix to append to a validation error message. Empty string for types
    /// without a schema (OverflowRequirements is free-form).
    /// </summary>
    internal static string SchemaHintSuffix(ArtifactType type)
    {
        var hint = GetSchemaHint(type);
        return string.IsNullOrEmpty(hint) ? string.Empty : $" Expected schema: {hint}";
    }

    private static string GetSchemaHint(ArtifactType type) => type switch
    {
        ArtifactType.RequirementsDocument =>
            """{"Title":"...","Description":"...","InScope":["..."],"OutOfScope":["..."]}""",
        ArtifactType.SprintPlan =>
            """{"Summary":"...","Phases":[{"Name":"...","Description":"...","Deliverables":["..."]}],"OverflowRequirements":["..."]}""",
        ArtifactType.ValidationReport =>
            """{"Verdict":"...","Findings":["..."],"RequiredChanges":["..."]}""",
        ArtifactType.SprintReport =>
            """{"Summary":"...","Delivered":["..."],"Learnings":["..."],"OverflowRequirements":["..."]}""",
        ArtifactType.SelfEvaluationReport =>
            """{"Attempt":1,"Items":[{"TaskId":"...","SuccessCriteria":"...","Verdict":"PASS|FAIL|UNVERIFIED","Evidence":"...","FixPlan":null}],"OverallVerdict":"AllPass|AnyFail|Unverified","Notes":null}""",
        _ => string.Empty,
    };

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
