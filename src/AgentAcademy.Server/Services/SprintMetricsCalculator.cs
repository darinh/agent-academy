using System.Text.Json;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Computes sprint metrics: per-sprint aggregation (duration, stage timing,
/// task/artifact counts) and workspace-level rollups. Read-only analytics
/// over sprint lifecycle events.
/// </summary>
public sealed class SprintMetricsCalculator : ISprintMetricsCalculator
{
    private readonly AgentAcademyDbContext _db;

    public SprintMetricsCalculator(AgentAcademyDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Computes aggregated metrics for a single sprint: duration, stage timing,
    /// task and artifact counts. Stage timing is derived from SprintStageAdvanced
    /// events in the activity log.
    /// </summary>
    public async Task<SprintMetrics?> GetSprintMetricsAsync(string sprintId)
    {
        var sprint = await _db.Sprints.FindAsync(sprintId);
        if (sprint is null) return null;

        _ = Enum.TryParse<SprintStatus>(sprint.Status, out var status);

        var artifactCount = await _db.SprintArtifacts
            .CountAsync(a => a.SprintId == sprintId);

        var taskCount = await _db.Tasks
            .CountAsync(t => t.SprintId == sprintId);

        var completedTaskCount = await _db.Tasks
            .CountAsync(t => t.SprintId == sprintId && t.Status == "Completed");

        var sprintEvents = await LoadSprintEventsAsync(sprintId);
        var timePerStage = ComputeTimePerStage(sprintEvents, sprint);
        var stageTransitions = CountStageTransitions(sprintEvents);

        var durationSeconds = sprint.CompletedAt.HasValue
            ? (sprint.CompletedAt.Value - sprint.CreatedAt).TotalSeconds
            : (double?)null;

        return new SprintMetrics(
            SprintId: sprintId,
            SprintNumber: sprint.Number,
            Status: status,
            DurationSeconds: durationSeconds,
            StageTransitions: stageTransitions,
            ArtifactCount: artifactCount,
            TaskCount: taskCount,
            CompletedTaskCount: completedTaskCount,
            TimePerStageSeconds: timePerStage,
            CreatedAt: sprint.CreatedAt,
            CompletedAt: sprint.CompletedAt);
    }

    /// <summary>
    /// Computes a workspace-level rollup across all sprints: counts, averages
    /// for duration and time per stage.
    /// </summary>
    public async Task<SprintMetricsSummary> GetMetricsSummaryAsync(string workspacePath)
    {
        var sprints = await _db.Sprints
            .Where(s => s.WorkspacePath == workspacePath)
            .ToListAsync();

        if (sprints.Count == 0)
        {
            return new SprintMetricsSummary(
                TotalSprints: 0, CompletedSprints: 0, CancelledSprints: 0, ActiveSprints: 0,
                AverageDurationSeconds: null, AverageTaskCount: 0, AverageArtifactCount: 0,
                AverageTimePerStageSeconds: new Dictionary<string, double>());
        }

        var completed = sprints.Count(s => s.Status == "Completed");
        var cancelled = sprints.Count(s => s.Status == "Cancelled");
        var active = sprints.Count(s => s.Status == "Active");

        var sprintIds = sprints.Select(s => s.Id).ToList();

        var totalArtifacts = await _db.SprintArtifacts
            .CountAsync(a => sprintIds.Contains(a.SprintId));

        var totalTasks = await _db.Tasks
            .CountAsync(t => t.SprintId != null && sprintIds.Contains(t.SprintId));

        var completedDurations = sprints
            .Where(s => s.Status == "Completed" && s.CompletedAt.HasValue)
            .Select(s => (s.CompletedAt!.Value - s.CreatedAt).TotalSeconds)
            .ToList();
        var avgDuration = completedDurations.Count > 0
            ? completedDurations.Average()
            : (double?)null;

        var allEvents = await LoadSprintEventsForIdsAsync(sprintIds);

        var allStageTimings = new Dictionary<string, List<double>>();
        foreach (var sprint in sprints)
        {
            var events = allEvents.GetValueOrDefault(sprint.Id) ?? [];
            var timing = ComputeTimePerStage(events, sprint);
            foreach (var (stage, seconds) in timing)
            {
                if (!allStageTimings.TryGetValue(stage, out var list))
                {
                    list = [];
                    allStageTimings[stage] = list;
                }
                list.Add(seconds);
            }
        }

        var avgTimePerStage = allStageTimings.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Average());

        return new SprintMetricsSummary(
            TotalSprints: sprints.Count,
            CompletedSprints: completed,
            CancelledSprints: cancelled,
            ActiveSprints: active,
            AverageDurationSeconds: avgDuration,
            AverageTaskCount: (double)totalTasks / sprints.Count,
            AverageArtifactCount: (double)totalArtifacts / sprints.Count,
            AverageTimePerStageSeconds: avgTimePerStage);
    }

    // ── Private helpers ─────────────────────────────────────────

    private async Task<List<ActivityEventEntity>> LoadSprintEventsAsync(string sprintId)
    {
        var events = await _db.ActivityEvents
            .Where(e => SprintEventTypes.Contains(e.Type))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

        return events
            .Where(e => BelongsToSprint(e, sprintId))
            .ToList();
    }

    private async Task<Dictionary<string, List<ActivityEventEntity>>> LoadSprintEventsForIdsAsync(
        List<string> sprintIds)
    {
        var events = await _db.ActivityEvents
            .Where(e => SprintEventTypes.Contains(e.Type))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

        var result = new Dictionary<string, List<ActivityEventEntity>>();
        foreach (var evt in events)
        {
            var sid = ExtractSprintId(evt);
            if (sid is null || !sprintIds.Contains(sid)) continue;
            if (!result.TryGetValue(sid, out var list))
            {
                list = [];
                result[sid] = list;
            }
            list.Add(evt);
        }
        return result;
    }

    private static readonly string[] SprintEventTypes =
    [
        nameof(ActivityEventType.SprintStarted),
        nameof(ActivityEventType.SprintStageAdvanced),
        nameof(ActivityEventType.SprintCompleted),
        nameof(ActivityEventType.SprintCancelled),
    ];

    private static Dictionary<string, double> ComputeTimePerStage(
        List<ActivityEventEntity> sprintEvents, SprintEntity sprint)
    {
        var result = new Dictionary<string, double>();

        if (sprintEvents.Count == 0)
        {
            var now = sprint.CompletedAt ?? DateTime.UtcNow;
            result[sprint.CurrentStage] = (now - sprint.CreatedAt).TotalSeconds;
            return result;
        }

        var currentStage = "Intake";
        var stageStart = sprint.CreatedAt;

        foreach (var evt in sprintEvents)
        {
            if (evt.Type == nameof(ActivityEventType.SprintStarted))
                continue;

            if (evt.Type is nameof(ActivityEventType.SprintStageAdvanced))
            {
                var meta = ParseMetadata(evt.MetadataJson);
                var action = meta.GetValueOrDefault("action")?.ToString();
                if (action == "signoff_requested") continue;

                var previousStage = meta.GetValueOrDefault("previousStage")?.ToString();
                var newStage = meta.GetValueOrDefault("currentStage")?.ToString();

                if (previousStage is not null)
                {
                    var elapsed = (evt.OccurredAt - stageStart).TotalSeconds;
                    result[previousStage] = result.GetValueOrDefault(previousStage) + elapsed;
                    stageStart = evt.OccurredAt;
                }

                if (newStage is not null)
                    currentStage = newStage;
            }

            if (evt.Type is nameof(ActivityEventType.SprintCompleted)
                or nameof(ActivityEventType.SprintCancelled))
            {
                var elapsed = (evt.OccurredAt - stageStart).TotalSeconds;
                result[currentStage] = result.GetValueOrDefault(currentStage) + elapsed;
                stageStart = evt.OccurredAt;
            }
        }

        if (sprint.Status == "Active")
        {
            var elapsed = (DateTime.UtcNow - stageStart).TotalSeconds;
            result[currentStage] = result.GetValueOrDefault(currentStage) + elapsed;
        }

        return result;
    }

    private static int CountStageTransitions(List<ActivityEventEntity> sprintEvents)
    {
        return sprintEvents
            .Where(e => e.Type == nameof(ActivityEventType.SprintStageAdvanced))
            .Count(e =>
            {
                var meta = ParseMetadata(e.MetadataJson);
                return meta.GetValueOrDefault("action")?.ToString() != "signoff_requested";
            });
    }

    private static bool BelongsToSprint(ActivityEventEntity evt, string sprintId)
    {
        return ExtractSprintId(evt) == sprintId;
    }

    private static string? ExtractSprintId(ActivityEventEntity evt)
    {
        if (string.IsNullOrEmpty(evt.MetadataJson)) return null;
        var meta = ParseMetadata(evt.MetadataJson);
        return meta.GetValueOrDefault("sprintId")?.ToString();
    }

    private static Dictionary<string, object?> ParseMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText(),
                };
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }
}
