using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// REST API for sprint lifecycle data — read-only views of sprint status,
/// history, and artifacts. Write operations happen through agent commands.
/// </summary>
[ApiController]
[Route("api/sprints")]
public class SprintController : ControllerBase
{
    private readonly ISprintService _sprintService;
    private readonly ISprintStageService _stageService;
    private readonly ISprintArtifactService _artifactService;
    private readonly ISprintMetricsCalculator _metricsCalculator;
    private readonly ISprintScheduleService _scheduleService;
    private readonly IRoomService _roomService;
    private readonly ILogger<SprintController> _logger;

    public SprintController(
        ISprintService sprintService,
        ISprintStageService stageService,
        ISprintArtifactService artifactService,
        ISprintMetricsCalculator metricsCalculator,
        ISprintScheduleService scheduleService,
        IRoomService roomService,
        ILogger<SprintController> logger)
    {
        _sprintService = sprintService;
        _stageService = stageService;
        _artifactService = artifactService;
        _metricsCalculator = metricsCalculator;
        _scheduleService = scheduleService;
        _roomService = roomService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/sprints — list sprints for the active workspace.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListSprints(
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0)
    {
        try
        {
            var workspace = await _roomService.GetActiveWorkspacePathAsync();
            if (workspace is null)
                return Ok(new SprintListResponse([], 0));

            var (sprints, totalCount) = await _sprintService.GetSprintsForWorkspaceAsync(workspace, limit, offset);
            var snapshots = sprints.Select(ToSnapshot).ToList();
            return Ok(new SprintListResponse(snapshots, totalCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list sprints");
            return Problem("Failed to retrieve sprint history.");
        }
    }

    /// <summary>
    /// GET /api/sprints/active — get the active sprint for the current workspace.
    /// Returns 204 No Content if no active sprint.
    /// </summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActiveSprint()
    {
        try
        {
            var workspace = await _roomService.GetActiveWorkspacePathAsync();
            if (workspace is null)
                return NoContent();

            var sprint = await _sprintService.GetActiveSprintAsync(workspace);
            if (sprint is null)
                return NoContent();

            var artifacts = await _artifactService.GetSprintArtifactsAsync(sprint.Id);
            return Ok(new SprintDetailResponse(
                ToSnapshot(sprint),
                artifacts.Select(ToArtifactSnapshot).ToList(),
                SprintStageService.Stages.ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active sprint");
            return Problem("Failed to retrieve active sprint.");
        }
    }

    /// <summary>
    /// GET /api/sprints/{id} — get a specific sprint with its artifacts.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetSprint(string id)
    {
        try
        {
            var sprint = await _sprintService.GetSprintByIdAsync(id);
            if (sprint is null)
                return NotFound();

            var artifacts = await _artifactService.GetSprintArtifactsAsync(sprint.Id);
            return Ok(new SprintDetailResponse(
                ToSnapshot(sprint),
                artifacts.Select(ToArtifactSnapshot).ToList(),
                SprintStageService.Stages.ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sprint {Id}", id);
            return Problem("Failed to retrieve sprint.");
        }
    }

    /// <summary>
    /// GET /api/sprints/{id}/artifacts — get artifacts for a sprint, optionally filtered by stage.
    /// </summary>
    [HttpGet("{id}/artifacts")]
    public async Task<IActionResult> GetArtifacts(string id, [FromQuery] string? stage = null)
    {
        try
        {
            var sprint = await _sprintService.GetSprintByIdAsync(id);
            if (sprint is null)
                return NotFound();

            var artifacts = await _artifactService.GetSprintArtifactsAsync(id, stage);
            return Ok(artifacts.Select(ToArtifactSnapshot).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artifacts for sprint {Id}", id);
            return Problem("Failed to retrieve sprint artifacts.");
        }
    }

    // ── Write Operations ────────────────────────────────────────

    /// <summary>
    /// POST /api/sprints — start a new sprint for the active workspace.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> StartSprint()
    {
        try
        {
            var workspace = await _roomService.GetActiveWorkspacePathAsync();
            if (workspace is null)
                return BadRequest(ApiProblem.BadRequest("No active workspace."));

            var sprint = await _sprintService.CreateSprintAsync(workspace);
            var artifacts = await _artifactService.GetSprintArtifactsAsync(sprint.Id);
            return Ok(new SprintDetailResponse(
                ToSnapshot(sprint),
                artifacts.Select(ToArtifactSnapshot).ToList(),
                SprintStageService.Stages.ToList()));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiProblem.Conflict(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start sprint");
            return Problem("Failed to start sprint.");
        }
    }

    /// <summary>
    /// POST /api/sprints/{id}/advance — advance the sprint to the next stage.
    /// </summary>
    [HttpPost("{id}/advance")]
    public async Task<IActionResult> AdvanceSprint(string id, [FromQuery] bool force = false)
    {
        try
        {
            var (_, ownerError) = await ValidateSprintOwnershipAsync(id);
            if (ownerError is not null) return ownerError;

            var sprint = await _stageService.AdvanceStageAsync(id, force);
            var artifacts = await _artifactService.GetSprintArtifactsAsync(sprint.Id);
            return Ok(new SprintDetailResponse(
                ToSnapshot(sprint),
                artifacts.Select(ToArtifactSnapshot).ToList(),
                SprintStageService.Stages.ToList()));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiProblem.Conflict(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to advance sprint {Id}", id);
            return Problem("Failed to advance sprint.");
        }
    }

    /// <summary>
    /// POST /api/sprints/{id}/complete — complete the sprint.
    /// </summary>
    [HttpPost("{id}/complete")]
    public async Task<IActionResult> CompleteSprint(string id, [FromQuery] bool force = false)
    {
        try
        {
            var (_, ownerError) = await ValidateSprintOwnershipAsync(id);
            if (ownerError is not null) return ownerError;

            var sprint = await _sprintService.CompleteSprintAsync(id, force);
            return Ok(ToSnapshot(sprint));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiProblem.Conflict(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete sprint {Id}", id);
            return Problem("Failed to complete sprint.");
        }
    }

    /// <summary>
    /// POST /api/sprints/{id}/cancel — cancel the sprint.
    /// </summary>
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> CancelSprint(string id)
    {
        try
        {
            var (_, ownerError) = await ValidateSprintOwnershipAsync(id);
            if (ownerError is not null) return ownerError;

            var sprint = await _sprintService.CancelSprintAsync(id);
            return Ok(ToSnapshot(sprint));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiProblem.Conflict(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel sprint {Id}", id);
            return Problem("Failed to cancel sprint.");
        }
    }

    /// <summary>
    /// POST /api/sprints/{id}/approve-advance — user approves pending stage advancement.
    /// </summary>
    [HttpPost("{id}/approve-advance")]
    public async Task<IActionResult> ApproveAdvance(string id)
    {
        try
        {
            var (_, ownerError) = await ValidateSprintOwnershipAsync(id);
            if (ownerError is not null) return ownerError;

            var sprint = await _stageService.ApproveAdvanceAsync(id);
            return Ok(new SprintDetailResponse(
                ToSnapshot(sprint),
                (await _artifactService.GetSprintArtifactsAsync(sprint.Id))
                    .Select(ToArtifactSnapshot).ToList(),
                SprintStageService.Stages.ToList()));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiProblem.Conflict(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve sprint advance {Id}", id);
            return Problem("Failed to approve sprint advance.");
        }
    }

    /// <summary>
    /// POST /api/sprints/{id}/reject-advance — user rejects pending stage advancement.
    /// </summary>
    [HttpPost("{id}/reject-advance")]
    public async Task<IActionResult> RejectAdvance(string id)
    {
        try
        {
            var (_, ownerError) = await ValidateSprintOwnershipAsync(id);
            if (ownerError is not null) return ownerError;

            var sprint = await _stageService.RejectAdvanceAsync(id);
            return Ok(ToSnapshot(sprint));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiProblem.Conflict(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reject sprint advance {Id}", id);
            return Problem("Failed to reject sprint advance.");
        }
    }

    /// <summary>
    /// GET /api/sprints/{id}/metrics — aggregated metrics for a single sprint.
    /// </summary>
    [HttpGet("{id}/metrics")]
    public async Task<IActionResult> GetSprintMetrics(string id)
    {
        try
        {
            var (_, ownerError) = await ValidateSprintOwnershipAsync(id);
            if (ownerError is not null) return ownerError;

            var metrics = await _metricsCalculator.GetSprintMetricsAsync(id);
            if (metrics is null) return NotFound();

            return Ok(metrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get metrics for sprint {Id}", id);
            return Problem("Failed to retrieve sprint metrics.");
        }
    }

    /// <summary>
    /// GET /api/sprints/metrics/summary — workspace-level rollup of sprint metrics.
    /// </summary>
    [HttpGet("metrics/summary")]
    public async Task<IActionResult> GetMetricsSummary()
    {
        try
        {
            var workspace = await _roomService.GetActiveWorkspacePathAsync();
            if (workspace is null)
                return Ok(new SprintMetricsSummary(0, 0, 0, 0, null, 0, 0,
                    new Dictionary<string, double>()));

            var summary = await _metricsCalculator.GetMetricsSummaryAsync(workspace);
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sprint metrics summary");
            return Problem("Failed to retrieve sprint metrics summary.");
        }
    }

    // ── Schedule CRUD ──────────────────────────────────────────

    /// <summary>
    /// GET /api/sprints/schedule — get the sprint schedule for the active workspace.
    /// </summary>
    [HttpGet("schedule")]
    public async Task<IActionResult> GetSchedule()
    {
        try
        {
            var workspace = await _roomService.GetActiveWorkspacePathAsync();
            if (workspace is null)
                return BadRequest(ApiProblem.BadRequest("No active workspace."));

            var schedule = await _scheduleService.GetScheduleAsync(workspace);
            if (schedule is null)
                return NotFound();

            return Ok(schedule);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sprint schedule");
            return Problem("Failed to retrieve sprint schedule.");
        }
    }

    /// <summary>
    /// PUT /api/sprints/schedule — create or update the sprint schedule for the active workspace.
    /// </summary>
    [HttpPut("schedule")]
    public async Task<IActionResult> UpsertSchedule([FromBody] SprintScheduleRequest request)
    {
        try
        {
            var workspace = await _roomService.GetActiveWorkspacePathAsync();
            if (workspace is null)
                return BadRequest(ApiProblem.BadRequest("No active workspace."));

            var response = await _scheduleService.UpsertScheduleAsync(
                workspace, request.CronExpression, request.TimeZoneId, request.Enabled);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiProblem.BadRequest(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert sprint schedule");
            return Problem("Failed to save sprint schedule.");
        }
    }

    /// <summary>
    /// DELETE /api/sprints/schedule — remove the sprint schedule for the active workspace.
    /// </summary>
    [HttpDelete("schedule")]
    public async Task<IActionResult> DeleteSchedule()
    {
        try
        {
            var workspace = await _roomService.GetActiveWorkspacePathAsync();
            if (workspace is null)
                return BadRequest(ApiProblem.BadRequest("No active workspace."));

            var deleted = await _scheduleService.DeleteScheduleAsync(workspace);
            if (!deleted)
                return NotFound();

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete sprint schedule");
            return Problem("Failed to delete sprint schedule.");
        }
    }

    private static SprintSnapshot ToSnapshot(Data.Entities.SprintEntity e)
    {
        _ = Enum.TryParse<SprintStatus>(e.Status, out var status);
        _ = Enum.TryParse<SprintStage>(e.CurrentStage, out var stage);
        _ = Enum.TryParse<SprintStage>(e.PendingStage ?? "", out var pendingStage);
        return new(e.Id, e.Number, status, stage,
            e.OverflowFromSprintId, e.AwaitingSignOff,
            e.PendingStage is not null ? pendingStage : null,
            e.SignOffRequestedAt, e.CreatedAt, e.CompletedAt);
    }

    /// <summary>
    /// Verifies the sprint belongs to the active workspace. Returns NotFound
    /// if the sprint doesn't exist or belongs to a different workspace.
    /// </summary>
    private async Task<(Data.Entities.SprintEntity? Sprint, IActionResult? Error)> ValidateSprintOwnershipAsync(string id)
    {
        var workspace = await _roomService.GetActiveWorkspacePathAsync();
        if (workspace is null)
            return (null, BadRequest(ApiProblem.BadRequest("No active workspace.")));

        var sprint = await _sprintService.GetSprintByIdAsync(id);
        if (sprint is null || sprint.WorkspacePath != workspace)
            return (null, NotFound());

        return (sprint, null);
    }

    private static SprintArtifact ToArtifactSnapshot(Data.Entities.SprintArtifactEntity a)
    {
        _ = Enum.TryParse<SprintStage>(a.Stage, out var stage);
        _ = Enum.TryParse<ArtifactType>(a.Type, out var type);
        return new(a.Id, a.SprintId, stage, type,
            a.Content, a.CreatedByAgentId, a.CreatedAt, a.UpdatedAt);
    }
}

/// <summary>Paginated list of sprint snapshots.</summary>
public record SprintListResponse(List<SprintSnapshot> Sprints, int TotalCount);

/// <summary>Full sprint detail including artifacts and stage definitions.</summary>
public record SprintDetailResponse(
    SprintSnapshot Sprint,
    List<SprintArtifact> Artifacts,
    List<string> Stages);
