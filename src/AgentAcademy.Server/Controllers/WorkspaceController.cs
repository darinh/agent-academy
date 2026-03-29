using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Workspace management — active workspace, listing, switching, scanning, and onboarding.
/// </summary>
[ApiController]
[Route("api")]
public class WorkspaceController : ControllerBase
{
    private readonly ProjectScanner _scanner;
    private readonly WorkspaceRuntime _runtime;
    private readonly AgentOrchestrator _orchestrator;
    private readonly IAgentExecutor _executor;
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<WorkspaceController> _logger;

    public WorkspaceController(
        ProjectScanner scanner,
        WorkspaceRuntime runtime,
        AgentOrchestrator orchestrator,
        IAgentExecutor executor,
        AgentAcademyDbContext db,
        ILogger<WorkspaceController> logger)
    {
        _scanner = scanner;
        _runtime = runtime;
        _orchestrator = orchestrator;
        _executor = executor;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/workspace — get the active workspace.
    /// </summary>
    [HttpGet("workspace")]
    public async Task<IActionResult> GetActiveWorkspace()
    {
        var entity = await _db.Workspaces.FirstOrDefaultAsync(w => w.IsActive);
        var active = entity is null ? null : ToMeta(entity);
        return Ok(new { active, dataDir = active?.Path });
    }

    /// <summary>
    /// GET /api/workspaces — list known workspaces.
    /// </summary>
    [HttpGet("workspaces")]
    public async Task<ActionResult<List<WorkspaceMeta>>> ListWorkspaces()
    {
        var entities = await _db.Workspaces
            .OrderByDescending(w => w.LastAccessedAt)
            .ToListAsync();
        return Ok(entities.Select(ToMeta).ToList());
    }

    /// <summary>
    /// PUT /api/workspace — switch the active workspace by path.
    /// </summary>
    [HttpPut("workspace")]
    public async Task<IActionResult> SetActiveWorkspace([FromBody] SwitchWorkspaceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { code = "missing_path", message = "path required" });

        if (!ValidatePath(request.Path, out var resolved, out var error))
            return BadRequest(new { code = "workspace_error", message = error });

        try
        {
            var previousWorkspace = await _runtime.GetActiveWorkspacePathAsync();
            var scan = _scanner.ScanProject(resolved);
            var meta = await UpsertWorkspaceAsync(scan.Path, scan.ProjectName);

            // On workspace switch: clear agent sessions and set up rooms for new project
            if (previousWorkspace != scan.Path)
            {
                await _executor.InvalidateAllSessionsAsync();
                await _runtime.EnsureDefaultRoomForWorkspaceAsync(scan.Path);
                _logger.LogInformation(
                    "Switched workspace from '{Previous}' to '{Current}' — sessions cleared, default room ensured",
                    previousWorkspace ?? "(none)", scan.Path);
            }

            return Ok(meta);
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(new { code = "workspace_error", message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch workspace to '{Path}'", resolved);
            return Problem("Failed to switch workspace.");
        }
    }

    /// <summary>
    /// POST /api/workspaces/scan — scan a directory for project metadata.
    /// </summary>
    [HttpPost("workspaces/scan")]
    public ActionResult<ProjectScanResult> ScanProject([FromBody] ScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { code = "missing_path", message = "path is required" });

        if (!ValidatePath(request.Path, out var resolved, out var error))
            return BadRequest(new { error });

        try
        {
            var result = _scanner.ScanProject(resolved);
            return Ok(result);
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { code = "not_found", message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan project at '{Path}'", resolved);
            return Problem("Failed to scan project.");
        }
    }

    /// <summary>
    /// POST /api/workspaces/onboard — onboard a project (scan + workspace metadata).
    /// When the project has no specs, automatically creates a specification
    /// generation task and kicks the orchestrator.
    /// </summary>
    [HttpPost("workspaces/onboard")]
    public async Task<ActionResult<OnboardResult>> OnboardProject([FromBody] ScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { code = "missing_path", message = "path is required" });

        if (!ValidatePath(request.Path, out var resolved, out var error))
            return BadRequest(new { error });

        try
        {
            var scan = _scanner.ScanProject(resolved);
            var meta = await UpsertWorkspaceAsync(scan.Path, scan.ProjectName);

            // Ensure the workspace has a default room with agents
            await _runtime.EnsureDefaultRoomForWorkspaceAsync(scan.Path);

            // Auto-create spec generation task when project has no specs
            if (!scan.HasSpecs)
            {
                var taskRequest = new TaskAssignmentRequest(
                    Title: "Generate Project Specification",
                    Description:
                        $"Analyze the codebase at '{resolved}' and generate a comprehensive project specification " +
                        "in the specs/ directory. The spec should cover: system overview, domain model, API contracts, " +
                        "services, data persistence, and any other relevant architectural concerns. Each spec section " +
                        "should follow the standard template with Purpose, Current Behavior, Interfaces & Contracts, " +
                        "Invariants, Known Gaps, and Revision History.",
                    SuccessCriteria:
                        "A complete specs/ directory with numbered sections covering all major system concerns",
                    RoomId: null,
                    PreferredRoles: new List<string> { "Planner", "TechnicalWriter" }
                );

                try
                {
                    var taskResult = await _runtime.CreateTaskAsync(taskRequest);
                    _orchestrator.HandleHumanMessage(taskResult.Room.Id);

                    return StatusCode(201, new OnboardResult(
                        Scan: scan,
                        Workspace: meta,
                        SpecTaskCreated: true,
                        RoomId: taskResult.Room.Id
                    ));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Spec task creation failed for '{Path}'; onboard succeeded without it", resolved);
                    return Ok(new OnboardResult(Scan: scan, Workspace: meta));
                }
            }

            return Ok(new OnboardResult(Scan: scan, Workspace: meta));
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { code = "not_found", message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to onboard project at '{Path}'", resolved);
            return Problem("Failed to onboard project.");
        }
    }

    /// <summary>
    /// Upserts a workspace in the DB and marks it as active.
    /// </summary>
    private async Task<WorkspaceMeta> UpsertWorkspaceAsync(string path, string? projectName)
    {
        var now = DateTime.UtcNow;

        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Deactivate all other workspaces
        await _db.Workspaces
            .Where(w => w.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.IsActive, false));

        var entity = await _db.Workspaces.FindAsync(path);
        if (entity is null)
        {
            entity = new WorkspaceEntity
            {
                Path = path,
                ProjectName = projectName,
                IsActive = true,
                LastAccessedAt = now,
                CreatedAt = now
            };
            _db.Workspaces.Add(entity);
        }
        else
        {
            entity.ProjectName = projectName;
            entity.IsActive = true;
            entity.LastAccessedAt = now;
        }

        await _db.SaveChangesAsync();

        // Cap at 20 workspaces (trim oldest after save so count is accurate)
        var count = await _db.Workspaces.CountAsync();
        if (count > 20)
        {
            var stale = await _db.Workspaces
                .Where(w => !w.IsActive)
                .OrderBy(w => w.LastAccessedAt)
                .Take(count - 20)
                .ToListAsync();
            _db.Workspaces.RemoveRange(stale);
            await _db.SaveChangesAsync();
        }

        await transaction.CommitAsync();
        return ToMeta(entity);
    }

    private static WorkspaceMeta ToMeta(WorkspaceEntity entity) =>
        new(Path: entity.Path, ProjectName: entity.ProjectName, LastAccessedAt: entity.LastAccessedAt);

    /// <summary>
    /// Validates that the resolved path is within the user's home directory.
    /// </summary>
    private static bool ValidatePath(string path, out string resolved, out string? error)
    {
        resolved = Path.GetFullPath(path);
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!resolved.StartsWith(homeDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && resolved != homeDir)
        {
            error = "Path must be within the user's home directory";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// Request body for scan and onboard endpoints.
/// </summary>
public record ScanRequest(string Path);

/// <summary>
/// Request body for switching active workspace.
/// </summary>
public record SwitchWorkspaceRequest(string Path);

/// <summary>
/// Result of onboarding a project.
/// </summary>
public record OnboardResult(
    ProjectScanResult Scan,
    WorkspaceMeta Workspace,
    bool SpecTaskCreated = false,
    string? RoomId = null
);
