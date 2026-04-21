using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentAcademy.Forge;
using AgentAcademy.Forge.Execution;
using AgentAcademy.Forge.Models;
using AgentAcademy.Server.Config;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Background service that coordinates forge pipeline runs.
/// Runs are enqueued via <see cref="StartRunAsync"/> and processed sequentially
/// by the background worker. Job status is tracked in memory.
/// </summary>
public sealed class ForgeRunService : BackgroundService, IForgeJobService
{
    private readonly PipelineRunner _pipelineRunner;
    private readonly ForgeOptions _options;
    private readonly ILogger<ForgeRunService> _logger;
    private readonly ConcurrentDictionary<string, ForgeJob> _jobs = new();
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(100);

    /// <summary>Regex for valid run IDs: R_ + 26 Crockford Base32 chars.</summary>
    private static readonly Regex RunIdPattern = new(@"^R_[0-9A-HJKMNP-TV-Z]{26}$", RegexOptions.Compiled);

    /// <summary>Regex for valid artifact hashes: 64 hex chars, optionally prefixed with sha256:.</summary>
    private static readonly Regex ArtifactHashPattern = new(@"^(sha256:)?[0-9a-f]{64}$", RegexOptions.Compiled);

    public ForgeRunService(
        PipelineRunner pipelineRunner,
        ForgeOptions options,
        ILogger<ForgeRunService> logger)
    {
        _pipelineRunner = pipelineRunner;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Enqueue a new forge run. Returns the job ID immediately.
    /// The run executes in the background.
    /// </summary>
    public Task<ForgeJob> StartRunAsync(TaskBrief task, MethodologyDefinition methodology)
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

        _jobs[job.JobId] = job;

        if (!_queue.Writer.TryWrite(job.JobId))
        {
            job.Status = ForgeJobStatus.Failed;
            job.Error = "Run queue is full. Try again later.";
            throw new InvalidOperationException(job.Error);
        }

        _logger.LogInformation("Forge job {JobId} queued for task {TaskId}", job.JobId, task.TaskId);
        return Task.FromResult(job);
    }

    /// <summary>Get a job by ID.</summary>
    public ForgeJob? GetJob(string jobId) => _jobs.GetValueOrDefault(jobId);

    /// <summary>List all jobs, most recent first.</summary>
    public IReadOnlyList<ForgeJob> ListJobs() =>
        _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ForgeRunService started");

        await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                continue;

            try
            {
                job.Status = ForgeJobStatus.Running;
                job.StartedAt = DateTime.UtcNow;

                _logger.LogInformation("Forge job {JobId} starting execution for task {TaskId}",
                    job.JobId, job.TaskBrief.TaskId);

                var result = await _pipelineRunner.ExecuteAsync(
                    job.TaskBrief, job.Methodology, stoppingToken);

                job.RunId = result.RunId;
                job.Status = result.Outcome == "succeeded"
                    ? ForgeJobStatus.Completed
                    : ForgeJobStatus.Failed;
                job.Error = result.Outcome == "succeeded" ? null : $"Pipeline outcome: {result.Outcome}";
                job.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation(
                    "Forge job {JobId} completed with outcome {Outcome}, runId {RunId}",
                    job.JobId, result.Outcome, result.RunId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Status = ForgeJobStatus.Failed;
                job.Error = "Cancelled due to server shutdown.";
                job.CompletedAt = DateTime.UtcNow;
                break;
            }
            catch (Exception ex)
            {
                job.Status = ForgeJobStatus.Failed;
                job.Error = ex.Message;
                job.CompletedAt = DateTime.UtcNow;

                _logger.LogError(ex, "Forge job {JobId} failed", job.JobId);
            }
        }
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
    Failed
}
