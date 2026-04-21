using System.ComponentModel.DataAnnotations;
using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Schemas;
using AgentAcademy.Forge.Storage;
using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// REST API for the Forge Pipeline Engine.
/// Read-only endpoints (runs, artifacts, schemas) are always available.
/// Execution endpoints (start run, resume) require an OpenAI API key.
/// </summary>
[ApiController]
[Route("api/forge")]
public sealed class ForgeController : ControllerBase
{
    private readonly ForgeRunService _runService;
    private readonly IRunStore _runStore;
    private readonly IArtifactStore _artifactStore;
    private readonly SchemaRegistry _schemaRegistry;
    private readonly IMethodologyCatalog _methodologyCatalog;
    private readonly ForgeOptions _options;

    public ForgeController(
        ForgeRunService runService,
        IRunStore runStore,
        IArtifactStore artifactStore,
        SchemaRegistry schemaRegistry,
        IMethodologyCatalog methodologyCatalog,
        ForgeOptions options)
    {
        _runService = runService;
        _runStore = runStore;
        _artifactStore = artifactStore;
        _schemaRegistry = schemaRegistry;
        _methodologyCatalog = methodologyCatalog;
        _options = options;
    }

    // ── Execution endpoints ─────────────────────────────────────────────────

    /// <summary>Start a new forge pipeline run.</summary>
    /// <returns>202 Accepted with job ID, or 503 if execution is unavailable.</returns>
    [HttpPost("jobs")]
    [Authorize]
    public async Task<IActionResult> StartRun([FromBody] StartForgeRunRequest request)
    {
        if (!_options.ExecutionAvailable)
            return Problem(
                title: "Forge execution unavailable",
                detail: "No OpenAI API key configured. Read-only endpoints remain available.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var task = new TaskBrief
        {
            TaskId = request.TaskId ?? Guid.NewGuid().ToString("N")[..8],
            Title = request.Title,
            Description = request.Description
        };

        var job = await _runService.StartRunAsync(task, request.Methodology);

        return AcceptedAtAction(nameof(GetJob), new { jobId = job.JobId }, new
        {
            job.JobId,
            job.Status,
            job.CreatedAt,
            taskId = task.TaskId
        });
    }

    /// <summary>Get status of a forge job.</summary>
    [HttpGet("jobs/{jobId}")]
    public async Task<IActionResult> GetJob(string jobId)
    {
        var job = await _runService.GetJobAsync(jobId);
        if (job is null)
            return NotFound(new { error = "Job not found", jobId });

        return Ok(new
        {
            job.JobId,
            job.RunId,
            Status = job.Status.ToString().ToLowerInvariant(),
            job.Error,
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt,
            TaskId = job.TaskBrief.TaskId,
            TaskTitle = job.TaskBrief.Title
        });
    }

    /// <summary>List all forge jobs.</summary>
    [HttpGet("jobs")]
    public async Task<IActionResult> ListJobs()
    {
        var jobs = await _runService.ListJobsAsync();
        return Ok(jobs.Select(j => new
        {
            j.JobId,
            j.RunId,
            Status = j.Status.ToString().ToLowerInvariant(),
            j.Error,
            j.CreatedAt,
            j.CompletedAt,
            TaskId = j.TaskBrief.TaskId,
            TaskTitle = j.TaskBrief.Title
        }));
    }

    /// <summary>Resume a crashed/failed forge run.</summary>
    [HttpPost("runs/{runId}/resume")]
    [Authorize]
    public IActionResult ResumeRun(string runId)
    {
        if (!ForgeRunService.IsValidRunId(runId))
            return BadRequest(new { error = "Invalid run ID format. Expected R_ + 26-char ULID." });

        if (!_options.ExecutionAvailable)
            return Problem(
                title: "Forge execution unavailable",
                detail: "No OpenAI API key configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        // Resume is not yet implemented — requires extending ForgeRunService
        return Problem(
            title: "Not implemented",
            detail: "Run resume is planned for a future release.",
            statusCode: StatusCodes.Status501NotImplemented);
    }

    // ── Read-only endpoints ─────────────────────────────────────────────────

    /// <summary>List all forge runs from the run store.</summary>
    [HttpGet("runs")]
    public async Task<IActionResult> ListRuns(CancellationToken ct)
    {
        var runIds = await _runStore.ListRunsAsync(ct);

        var runs = new List<object>();
        foreach (var runId in runIds)
        {
            var trace = await _runStore.ReadRunAsync(runId, ct);
            if (trace is not null)
            {
                runs.Add(new
                {
                    trace.RunId,
                    trace.TaskId,
                    trace.MethodologyVersion,
                    trace.Outcome,
                    trace.StartedAt,
                    trace.EndedAt,
                    trace.PipelineCost,
                    PhaseCount = trace.FinalArtifactHashes.Count,
                    trace.FidelityOutcome
                });
            }
        }

        return Ok(runs);
    }

    /// <summary>Get full run trace by ID.</summary>
    [HttpGet("runs/{runId}")]
    public async Task<IActionResult> GetRun(string runId, CancellationToken ct)
    {
        if (!ForgeRunService.IsValidRunId(runId))
            return BadRequest(new { error = "Invalid run ID format. Expected R_ + 26-char ULID." });

        var trace = await _runStore.ReadRunAsync(runId, ct);
        if (trace is null)
            return NotFound(new { error = "Run not found", runId });

        return Ok(trace);
    }

    /// <summary>Get phase-level traces for a run.</summary>
    [HttpGet("runs/{runId}/phases")]
    public async Task<IActionResult> GetRunPhases(string runId, CancellationToken ct)
    {
        if (!ForgeRunService.IsValidRunId(runId))
            return BadRequest(new { error = "Invalid run ID format. Expected R_ + 26-char ULID." });

        var phases = await _runStore.ReadPhaseRunsRollupAsync(runId, ct);
        if (phases is null)
            return NotFound(new { error = "Run or phase data not found", runId });

        return Ok(phases);
    }

    /// <summary>Get an artifact by its content hash.</summary>
    [HttpGet("artifacts/{hash}")]
    public async Task<IActionResult> GetArtifact(string hash, CancellationToken ct)
    {
        var normalized = ForgeRunService.NormalizeArtifactHash(hash);
        if (normalized is null)
            return BadRequest(new { error = "Invalid artifact hash. Expected 64 hex chars, optionally prefixed with sha256:." });

        var envelope = await _artifactStore.ReadAsync(normalized, ct);
        if (envelope is null)
            return NotFound(new { error = "Artifact not found", hash });

        var meta = await _artifactStore.ReadMetaAsync(normalized, ct);
        return Ok(new { artifact = envelope, meta });
    }

    /// <summary>List all registered artifact schemas.</summary>
    [HttpGet("schemas")]
    public IActionResult ListSchemas()
    {
        var schemas = _schemaRegistry.SchemaIds
            .Select(id => _schemaRegistry.GetSchema(id))
            .Select(s => new
            {
                Id = s.SchemaId,
                s.ArtifactType,
                s.SchemaVersion,
                s.Status,
                SemanticRuleCount = s.SemanticRules.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            })
            .ToList();

        return Ok(schemas);
    }

    /// <summary>Get forge engine status.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var jobs = await _runService.ListJobsAsync();
        return Ok(new
        {
            Enabled = _options.Enabled,
            _options.ExecutionAvailable,
            RunsDirectory = _options.RunsDirectory,
            ActiveJobs = jobs.Count(j => j.Status is ForgeJobStatus.Queued or ForgeJobStatus.Running),
            TotalJobs = jobs.Count,
            CompletedJobs = jobs.Count(j => j.Status == ForgeJobStatus.Completed),
            FailedJobs = jobs.Count(j => j.Status == ForgeJobStatus.Failed)
        });
    }

    // ── Methodology catalog endpoints ───────────────────────────────────────

    /// <summary>List all saved methodology templates.</summary>
    [HttpGet("methodologies")]
    public async Task<IActionResult> ListMethodologies(CancellationToken ct)
    {
        var methodologies = await _methodologyCatalog.ListAsync(ct);
        return Ok(methodologies);
    }

    /// <summary>Get a saved methodology by ID.</summary>
    [HttpGet("methodologies/{methodologyId}")]
    public async Task<IActionResult> GetMethodology(string methodologyId, CancellationToken ct)
    {
        var methodology = await _methodologyCatalog.GetAsync(methodologyId, ct);
        if (methodology is null)
            return NotFound(new { error = "Methodology not found", methodologyId });

        return Ok(methodology);
    }

    /// <summary>Save or update a methodology template.</summary>
    [HttpPut("methodologies/{methodologyId}")]
    [Authorize]
    public async Task<IActionResult> SaveMethodology(
        string methodologyId,
        [FromBody] MethodologyDefinition methodology,
        CancellationToken ct)
    {
        if (methodology.Id != methodologyId)
            return BadRequest(new { error = "Methodology ID in body must match the URL parameter." });

        try
        {
            var savedId = await _methodologyCatalog.SaveAsync(methodology, ct);
            return Ok(new { id = savedId, message = "Methodology saved." });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

/// <summary>Request body for starting a forge run.</summary>
public sealed class StartForgeRunRequest
{
    /// <summary>Optional task ID. Auto-generated if omitted.</summary>
    public string? TaskId { get; set; }

    /// <summary>Task title (required).</summary>
    [Required]
    public required string Title { get; set; }

    /// <summary>Task description (required).</summary>
    [Required]
    public required string Description { get; set; }

    /// <summary>Methodology definition (required).</summary>
    [Required]
    public required MethodologyDefinition Methodology { get; set; }
}
