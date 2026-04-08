using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// REST API for sprint lifecycle data — read-only views of sprint status,
/// history, and artifacts. Write operations happen through agent commands.
/// </summary>
[ApiController]
[Route("api/sprints")]
public class SprintController : ControllerBase
{
    private readonly SprintService _sprintService;
    private readonly WorkspaceRuntime _runtime;
    private readonly ILogger<SprintController> _logger;

    public SprintController(
        SprintService sprintService,
        WorkspaceRuntime runtime,
        ILogger<SprintController> logger)
    {
        _sprintService = sprintService;
        _runtime = runtime;
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
            var workspace = await _runtime.GetActiveWorkspacePathAsync();
            if (workspace is null)
                return Ok(new SprintListResponse([], 0));

            var sprints = await _sprintService.GetSprintsForWorkspaceAsync(workspace, limit, offset);
            var snapshots = sprints.Select(ToSnapshot).ToList();
            return Ok(new SprintListResponse(snapshots, snapshots.Count));
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
            var workspace = await _runtime.GetActiveWorkspacePathAsync();
            if (workspace is null)
                return NoContent();

            var sprint = await _sprintService.GetActiveSprintAsync(workspace);
            if (sprint is null)
                return NoContent();

            var artifacts = await _sprintService.GetSprintArtifactsAsync(sprint.Id);
            return Ok(new SprintDetailResponse(
                ToSnapshot(sprint),
                artifacts.Select(ToArtifactSnapshot).ToList(),
                SprintService.Stages.ToList()));
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

            var artifacts = await _sprintService.GetSprintArtifactsAsync(sprint.Id);
            return Ok(new SprintDetailResponse(
                ToSnapshot(sprint),
                artifacts.Select(ToArtifactSnapshot).ToList(),
                SprintService.Stages.ToList()));
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

            var artifacts = await _sprintService.GetSprintArtifactsAsync(id, stage);
            return Ok(artifacts.Select(ToArtifactSnapshot).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artifacts for sprint {Id}", id);
            return Problem("Failed to retrieve sprint artifacts.");
        }
    }

    private static SprintSnapshot ToSnapshot(Data.Entities.SprintEntity e)
    {
        _ = Enum.TryParse<SprintStatus>(e.Status, out var status);
        _ = Enum.TryParse<SprintStage>(e.CurrentStage, out var stage);
        return new(e.Id, e.Number, status, stage,
            e.OverflowFromSprintId, e.CreatedAt, e.CompletedAt);
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
