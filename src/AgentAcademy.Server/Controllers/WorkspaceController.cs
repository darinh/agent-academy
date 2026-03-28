using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

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
    private readonly ILogger<WorkspaceController> _logger;

    /// <summary>
    /// Tracks the active workspace in-memory. A proper WorkspaceManager service
    /// will replace this when workspace persistence is ported.
    /// </summary>
    private static volatile WorkspaceMeta? _activeWorkspace;
    private static readonly List<WorkspaceMeta> _workspaces = new();
    private static readonly object _lock = new();

    public WorkspaceController(
        ProjectScanner scanner,
        WorkspaceRuntime runtime,
        AgentOrchestrator orchestrator,
        ILogger<WorkspaceController> logger)
    {
        _scanner = scanner;
        _runtime = runtime;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/workspace — get the active workspace.
    /// </summary>
    [HttpGet("workspace")]
    public IActionResult GetActiveWorkspace()
    {
        return Ok(new
        {
            active = _activeWorkspace,
            dataDir = _activeWorkspace?.Path
        });
    }

    /// <summary>
    /// GET /api/workspaces — list known workspaces.
    /// </summary>
    [HttpGet("workspaces")]
    public ActionResult<List<WorkspaceMeta>> ListWorkspaces()
    {
        lock (_lock)
        {
            return Ok(_workspaces.ToList());
        }
    }

    /// <summary>
    /// PUT /api/workspace — switch the active workspace by path.
    /// </summary>
    [HttpPut("workspace")]
    public IActionResult SetActiveWorkspace([FromBody] SwitchWorkspaceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { code = "missing_path", message = "path required" });

        if (!ValidatePath(request.Path, out var resolved, out var error))
            return BadRequest(new { code = "workspace_error", message = error });

        try
        {
            var scan = _scanner.ScanProject(resolved);
            var meta = new WorkspaceMeta(
                Path: scan.Path,
                ProjectName: scan.ProjectName,
                LastAccessedAt: DateTime.UtcNow
            );

            lock (_lock)
            {
                // Remove existing entry for this path if present
                _workspaces.RemoveAll(w =>
                    string.Equals(w.Path, meta.Path, StringComparison.Ordinal));
                _workspaces.Insert(0, meta);

                // Cap at 20 recent workspaces
                while (_workspaces.Count > 20)
                    _workspaces.RemoveAt(_workspaces.Count - 1);

                _activeWorkspace = meta;
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
            var meta = new WorkspaceMeta(
                Path: scan.Path,
                ProjectName: scan.ProjectName,
                LastAccessedAt: DateTime.UtcNow
            );

            // Set as active workspace and track it
            lock (_lock)
            {
                _workspaces.RemoveAll(w =>
                    string.Equals(w.Path, meta.Path, StringComparison.Ordinal));
                _workspaces.Insert(0, meta);

                // Cap at 20 recent workspaces
                while (_workspaces.Count > 20)
                    _workspaces.RemoveAt(_workspaces.Count - 1);

                _activeWorkspace = meta;
            }

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
