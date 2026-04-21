using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentAcademy.Forge;
using AgentAcademy.Forge.Execution;
using AgentAcademy.Forge.Models;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Background service that coordinates forge pipeline runs.
/// Runs are enqueued via <see cref="StartRunAsync"/> and processed sequentially
/// by the background worker. Job status is persisted to SQLite for durability
/// across restarts, with an in-memory cache for active (Queued/Running) jobs.
/// </summary>
public sealed class ForgeRunService : BackgroundService, IForgeJobService
{
    private readonly PipelineRunner _pipelineRunner;
    private readonly ForgeOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IActivityBroadcaster _activityBus;
    private readonly ILogger<ForgeRunService> _logger;
    private readonly ConcurrentDictionary<string, ForgeJob> _activeJobs = new();
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(100);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Regex for valid run IDs: R_ + 26 Crockford Base32 chars.</summary>
    private static readonly Regex RunIdPattern = new(@"^R_[0-9A-HJKMNP-TV-Z]{26}$", RegexOptions.Compiled);

    /// <summary>Regex for valid artifact hashes: 64 hex chars, optionally prefixed with sha256:.</summary>
    private static readonly Regex ArtifactHashPattern = new(@"^(sha256:)?[0-9a-f]{64}$", RegexOptions.Compiled);

    public ForgeRunService(
        PipelineRunner pipelineRunner,
        ForgeOptions options,
        IServiceScopeFactory scopeFactory,
        IActivityBroadcaster activityBus,
        ILogger<ForgeRunService> logger)
    {
        _pipelineRunner = pipelineRunner;
        _options = options;
        _scopeFactory = scopeFactory;
        _activityBus = activityBus;
        _logger = logger;
    }

    /// <summary>
    /// Enqueue a new forge run. Persists to DB, then enqueues for background execution.
    /// Returns the job ID immediately.
    /// </summary>
    public async Task<ForgeJob> StartRunAsync(TaskBrief task, MethodologyDefinition methodology)
    {
        if (!_options.ExecutionAvailable)
            throw new InvalidOperationException("Forge execution is unavailable — no OpenAI API key configured.");

        var job = new ForgeJob
        {
            JobId = Guid.NewGuid().ToString("N")[..12],
            TaskBrief = task,
            Methodology = methodology,
            Status = ForgeJobStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };

        // Persist before enqueuing — crash-safe ordering
        await PersistJobAsync(job);
        _activeJobs[job.JobId] = job;

        if (!_queue.Writer.TryWrite(job.JobId))
        {
            job.Status = ForgeJobStatus.Failed;
            job.Error = "Run queue is full. Try again later.";
            await UpdateJobStatusAsync(job);
            _activeJobs.TryRemove(job.JobId, out _);
            throw new InvalidOperationException(job.Error);
        }

        _logger.LogInformation("Forge job {JobId} queued for task {TaskId}", job.JobId, task.TaskId);
        BroadcastForgeEvent(ActivityEventType.ForgeJobQueued, job.JobId, $"Forge job queued: {task.Title}");
        return job;
    }

    /// <summary>Get a job by ID — checks active cache first, falls back to DB.</summary>
    public async Task<ForgeJob?> GetJobAsync(string jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var active))
            return active;

        return await LoadJobFromDbAsync(jobId);
    }

    /// <summary>List all jobs from DB, most recent first.</summary>
    public async Task<IReadOnlyList<ForgeJob>> ListJobsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var entities = await db.ForgeJobs
            .AsNoTracking()
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        return entities.Select(EntityToJob).ToList();
    }

    /// <summary>Validate a run ID format (R_ + ULID).</summary>
    public static bool IsValidRunId(string? runId) =>
        !string.IsNullOrEmpty(runId) && RunIdPattern.IsMatch(runId);

    /// <summary>
    /// Validate and normalize an artifact hash. Strips optional sha256: prefix.
    /// Returns the raw 64-char hex hash, or null if invalid.
    /// </summary>
    public static string? NormalizeArtifactHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || !ArtifactHashPattern.IsMatch(hash))
            return null;

        return hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash[7..] : hash;
    }

    /// <summary>
    /// Recover jobs from a previous server lifecycle.
    /// Queued jobs are re-enqueued if execution is available and queue has capacity;
    /// Running jobs are marked as interrupted.
    /// Call during app initialization, before serving traffic.
    /// </summary>
    public async Task RecoverJobsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        // Mark Running jobs as interrupted — we can't resume mid-pipeline
        var runningJobs = await db.ForgeJobs
            .Where(e => e.Status == "running")
            .ToListAsync();

        foreach (var entity in runningJobs)
        {
            entity.Status = "interrupted";
            entity.Error = "Server restarted during execution.";
            entity.CompletedAt = DateTime.UtcNow;
            _logger.LogWarning("Forge job {JobId} marked as interrupted after server restart", entity.Id);
        }

        if (runningJobs.Count > 0)
            await db.SaveChangesAsync();

        // Re-enqueue Queued jobs only if execution is available
        var queuedJobs = await db.ForgeJobs
            .Where(e => e.Status == "queued")
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();

        if (queuedJobs.Count == 0)
        {
            if (runningJobs.Count > 0)
                _logger.LogInformation("Forge job recovery: {Interrupted} interrupted", runningJobs.Count);
            return;
        }

        if (!_options.ExecutionAvailable)
        {
            foreach (var entity in queuedJobs)
            {
                entity.Status = "interrupted";
                entity.Error = "Server restarted and execution is unavailable (no API key configured).";
                entity.CompletedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            _logger.LogWarning(
                "Forge job recovery: {Count} queued jobs marked interrupted — execution unavailable",
                queuedJobs.Count);
            return;
        }

        var requeued = 0;
        foreach (var entity in queuedJobs)
        {
            var job = EntityToJob(entity);
            _activeJobs[job.JobId] = job;

            if (!_queue.Writer.TryWrite(job.JobId))
            {
                // Queue full — leave as queued in DB for next restart, don't permanently fail
                _activeJobs.TryRemove(job.JobId, out _);
                _logger.LogWarning(
                    "Could not re-enqueue recovered job {JobId} — queue full; will retry on next restart",
                    job.JobId);
            }
            else
            {
                requeued++;
                _logger.LogInformation("Recovered and re-enqueued forge job {JobId}", job.JobId);
            }
        }

        // Only save if we changed something (running jobs)
        _logger.LogInformation(
            "Forge job recovery: {Interrupted} interrupted, {Requeued} re-enqueued, {Deferred} deferred",
            runningJobs.Count, requeued, queuedJobs.Count - requeued);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ForgeRunService started");

        await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_activeJobs.TryGetValue(jobId, out var job))
                continue;

            try
            {
                job.Status = ForgeJobStatus.Running;
                job.StartedAt = DateTime.UtcNow;
                await UpdateJobStatusAsync(job);
                BroadcastForgeEvent(ActivityEventType.ForgeJobStarted, job.JobId,
                    $"Forge job started: {job.TaskBrief.Title}");

                _logger.LogInformation("Forge job {JobId} starting execution for task {TaskId}",
                    job.JobId, job.TaskBrief.TaskId);

                var progress = new Progress<ForgeProgressEvent>(evt => OnForgeProgress(job.JobId, evt));
                var result = await _pipelineRunner.ExecuteAsync(
                    job.TaskBrief, job.Methodology, stoppingToken, progress);

                job.RunId = result.RunId;
                job.Status = result.Outcome == "succeeded"
                    ? ForgeJobStatus.Completed
                    : ForgeJobStatus.Failed;
                job.Error = result.Outcome == "succeeded" ? null : $"Pipeline outcome: {result.Outcome}";
                job.CompletedAt = DateTime.UtcNow;

                await UpdateJobStatusAsync(job);
                _activeJobs.TryRemove(jobId, out _);

                var eventType = result.Outcome == "succeeded"
                    ? ActivityEventType.ForgeJobCompleted
                    : ActivityEventType.ForgeJobFailed;
                BroadcastForgeEvent(eventType, job.JobId,
                    $"Forge job {result.Outcome}: {job.TaskBrief.Title}",
                    new Dictionary<string, object?> { ["runId"] = result.RunId, ["outcome"] = result.Outcome });

                _logger.LogInformation(
                    "Forge job {JobId} completed with outcome {Outcome}, runId {RunId}",
                    job.JobId, result.Outcome, result.RunId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Status = ForgeJobStatus.Failed;
                job.Error = "Cancelled due to server shutdown.";
                job.CompletedAt = DateTime.UtcNow;
                await TryUpdateJobStatusAsync(job);
                _activeJobs.TryRemove(jobId, out _);
                break;
            }
            catch (Exception ex)
            {
                job.Status = ForgeJobStatus.Failed;
                job.Error = ex.Message;
                job.CompletedAt = DateTime.UtcNow;
                await TryUpdateJobStatusAsync(job);
                _activeJobs.TryRemove(jobId, out _);

                _logger.LogError(ex, "Forge job {JobId} failed", job.JobId);
            }
        }
    }

    private async Task PersistJobAsync(ForgeJob job)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        db.ForgeJobs.Add(new ForgeJobEntity
        {
            Id = job.JobId,
            RunId = job.RunId,
            Status = job.Status.ToString().ToLowerInvariant(),
            Error = job.Error,
            TaskBriefJson = JsonSerializer.Serialize(job.TaskBrief, JsonOptions),
            MethodologyJson = JsonSerializer.Serialize(job.Methodology, JsonOptions),
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt
        });

        await db.SaveChangesAsync();
    }

    private async Task UpdateJobStatusAsync(ForgeJob job)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var entity = await db.ForgeJobs.FindAsync(job.JobId);
        if (entity is null) return;

        entity.RunId = job.RunId;
        entity.Status = job.Status.ToString().ToLowerInvariant();
        entity.Error = job.Error;
        entity.StartedAt = job.StartedAt;
        entity.CompletedAt = job.CompletedAt;

        await db.SaveChangesAsync();
    }

    /// <summary>Best-effort status update — swallows exceptions during shutdown.</summary>
    private async Task TryUpdateJobStatusAsync(ForgeJob job)
    {
        try
        {
            await UpdateJobStatusAsync(job);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist final status for forge job {JobId}", job.JobId);
        }
    }

    private async Task<ForgeJob?> LoadJobFromDbAsync(string jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var entity = await db.ForgeJobs.AsNoTracking().FirstOrDefaultAsync(e => e.Id == jobId);
        return entity is null ? null : EntityToJob(entity);
    }

    private static ForgeJob EntityToJob(ForgeJobEntity entity)
    {
        TaskBrief taskBrief;
        try
        {
            taskBrief = JsonSerializer.Deserialize<TaskBrief>(entity.TaskBriefJson, JsonOptions)
                        ?? new TaskBrief { TaskId = "unknown", Title = "Unknown", Description = "" };
        }
        catch (JsonException)
        {
            taskBrief = new TaskBrief { TaskId = "unknown", Title = "Unknown (corrupt data)", Description = "" };
        }

        MethodologyDefinition methodology;
        try
        {
            methodology = JsonSerializer.Deserialize<MethodologyDefinition>(entity.MethodologyJson, JsonOptions)
                          ?? new MethodologyDefinition { Id = "unknown", Phases = [] };
        }
        catch (JsonException)
        {
            methodology = new MethodologyDefinition { Id = "unknown", Phases = [] };
        }

        var status = Enum.TryParse<ForgeJobStatus>(entity.Status, ignoreCase: true, out var parsed)
            ? parsed
            : ForgeJobStatus.Failed;

        return new ForgeJob
        {
            JobId = entity.Id,
            RunId = entity.RunId,
            TaskBrief = taskBrief,
            Methodology = methodology,
            Status = status,
            Error = entity.Error,
            CreatedAt = entity.CreatedAt,
            StartedAt = entity.StartedAt,
            CompletedAt = entity.CompletedAt
        };
    }

    private void OnForgeProgress(string jobId, ForgeProgressEvent evt)
    {
        var (eventType, message) = evt.Kind switch
        {
            ForgeProgressKind.WaveStarted => (ActivityEventType.ForgePhaseStarted, evt.Message ?? "Wave started"),
            ForgeProgressKind.PhaseCompleted => (ActivityEventType.ForgePhaseCompleted, $"Phase {evt.PhaseId} completed"),
            ForgeProgressKind.PhaseFailed => (ActivityEventType.ForgePhaseFailed, $"Phase {evt.PhaseId} failed"),
            ForgeProgressKind.PhaseStarted => (ActivityEventType.ForgePhaseStarted, $"Phase {evt.PhaseId} started"),
            _ => (default(ActivityEventType?), (string?)null)
        };

        if (eventType is null) return;

        var metadata = new Dictionary<string, object?>
        {
            ["jobId"] = jobId,
            ["runId"] = evt.RunId,
            ["phaseId"] = evt.PhaseId,
            ["wave"] = evt.Wave,
            ["kind"] = evt.Kind.ToString()
        };

        BroadcastForgeEvent(eventType.Value, jobId, message!, metadata);
    }

    private void BroadcastForgeEvent(ActivityEventType type, string jobId, string message,
        Dictionary<string, object?>? metadata = null)
    {
        metadata ??= new Dictionary<string, object?>();
        metadata["jobId"] = jobId;

        _activityBus.Broadcast(new ActivityEvent(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            Severity: ActivitySeverity.Info,
            RoomId: null,
            ActorId: null,
            TaskId: null,
            Message: message,
            CorrelationId: jobId,
            OccurredAt: DateTime.UtcNow,
            Metadata: metadata
        ));
    }
}

/// <summary>Tracks a forge pipeline run request.</summary>
public sealed class ForgeJob
{
    public required string JobId { get; init; }
    public string? RunId { get; set; }
    public required TaskBrief TaskBrief { get; init; }
    public required MethodologyDefinition Methodology { get; init; }
    public ForgeJobStatus Status { get; set; }
    public string? Error { get; set; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum ForgeJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Interrupted
}
