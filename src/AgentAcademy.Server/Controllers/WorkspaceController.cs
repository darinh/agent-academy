using System.ComponentModel.DataAnnotations;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Workspace management — active workspace, listing, switching, scanning, and onboarding.
/// </summary>
[ApiController]
[Route("api")]
public class WorkspaceController : ControllerBase
{
    private readonly ProjectScanner _scanner;
    private readonly IRoomService _roomService;
    private readonly IWorkspaceRoomService _workspaceRooms;
    private readonly IWorkspaceService _workspaceService;
    private readonly ITaskOrchestrationService _taskOrchestration;
    private readonly ITaskQueryService _taskQueries;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IAgentExecutor _executor;
    private readonly IConversationSessionService _sessionService;
    private readonly ILogger<WorkspaceController> _logger;

    public WorkspaceController(
        ProjectScanner scanner,
        IRoomService roomService,
        IWorkspaceRoomService workspaceRooms,
        IWorkspaceService workspaceService,
        ITaskOrchestrationService taskOrchestration,
        ITaskQueryService taskQueries,
        IAgentOrchestrator orchestrator,
        IAgentExecutor executor,
        IConversationSessionService sessionService,
        ILogger<WorkspaceController> logger)
    {
        _scanner = scanner;
        _roomService = roomService;
        _workspaceRooms = workspaceRooms;
        _workspaceService = workspaceService;
        _taskOrchestration = taskOrchestration;
        _taskQueries = taskQueries;
        _orchestrator = orchestrator;
        _executor = executor;
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/workspace — get the active workspace.
    /// </summary>
    [HttpGet("workspace")]
    public async Task<IActionResult> GetActiveWorkspace()
    {
        var active = await _workspaceService.GetActiveWorkspaceAsync();
        return Ok(new { active, dataDir = active?.Path });
    }

    /// <summary>
    /// GET /api/workspaces — list known workspaces.
    /// </summary>
    [HttpGet("workspaces")]
    public async Task<ActionResult<List<WorkspaceMeta>>> ListWorkspaces()
    {
        var workspaces = await _workspaceService.ListWorkspacesAsync();
        return Ok(workspaces);
    }

    /// <summary>
    /// PUT /api/workspace — switch the active workspace by path.
    /// </summary>
    [HttpPut("workspace")]
    public async Task<IActionResult> SetActiveWorkspace([FromBody] SwitchWorkspaceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(ApiProblem.BadRequest("path required", "missing_path"));

        if (!ValidatePath(request.Path, out var resolved, out var error))
            return BadRequest(ApiProblem.BadRequest(error ?? "Invalid path", "workspace_error"));

        try
        {
            var previousWorkspace = await _roomService.GetActiveWorkspacePathAsync();
            var scan = _scanner.ScanProject(resolved);
            var meta = await _workspaceService.ActivateWorkspaceAsync(scan);

            // On workspace switch: archive sessions with summaries, then clear SDK state
            if (previousWorkspace != scan.Path)
            {
                // Archive active conversation sessions with LLM summaries so agents
                // can resume context when the user returns to this project
                try
                {
                    var archived = await _sessionService.ArchiveAllActiveSessionsAsync();
                    if (archived > 0)
                        _logger.LogInformation(
                            "Archived {Count} conversation sessions before workspace switch", archived);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to archive sessions before workspace switch — sessions will be lost");
                }

                await _executor.InvalidateAllSessionsAsync();
                await _workspaceRooms.EnsureDefaultRoomForWorkspaceAsync(scan.Path);
                _logger.LogInformation(
                    "Switched workspace from '{Previous}' to '{Current}' — sessions archived and cleared, default room ensured",
                    previousWorkspace ?? "(none)", scan.Path);
            }

            return Ok(meta);
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(ApiProblem.BadRequest(ex.Message, "workspace_error"));
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
            return BadRequest(ApiProblem.BadRequest("path is required", "missing_path"));

        if (!ValidatePath(request.Path, out var resolved, out var error))
            return BadRequest(ApiProblem.BadRequest(error ?? "Invalid path", "invalid_path"));

        try
        {
            var result = _scanner.ScanProject(resolved);
            return Ok(result);
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(ApiProblem.NotFound(ex.Message, "not_found"));
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
            return BadRequest(ApiProblem.BadRequest("path is required", "missing_path"));

        if (!ValidatePath(request.Path, out var resolved, out var error))
            return BadRequest(ApiProblem.BadRequest(error ?? "Invalid path", "invalid_path"));

        try
        {
            var scan = _scanner.ScanProject(resolved);
            var meta = await _workspaceService.ActivateWorkspaceAsync(scan);

            // Ensure the workspace has a default room with agents
            await _workspaceRooms.EnsureDefaultRoomForWorkspaceAsync(scan.Path);

            // Auto-create spec generation task when project has no specs
            if (!scan.HasSpecs)
            {
                // Check if a spec task already exists to avoid duplicates on repeated onboards
                var existingSpecTask = await _taskQueries.FindTaskByTitleAsync("Generate Project Specification");
                if (existingSpecTask is null)
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
                    var taskResult = await _taskOrchestration.CreateTaskAsync(taskRequest);
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
            }

            return Ok(new OnboardResult(Scan: scan, Workspace: meta));
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(ApiProblem.NotFound(ex.Message, "not_found"));
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
public record ScanRequest([Required, StringLength(1000)] string Path);

/// <summary>
/// Request body for switching active workspace.
/// </summary>
public record SwitchWorkspaceRequest([Required, StringLength(1000)] string Path);

/// <summary>
/// Result of onboarding a project.
/// </summary>
public record OnboardResult(
    ProjectScanResult Scan,
    WorkspaceMeta Workspace,
    bool SpecTaskCreated = false,
    string? RoomId = null
);
