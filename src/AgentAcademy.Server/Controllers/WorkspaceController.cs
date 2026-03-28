using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Workspace management — scanning and onboarding projects.
/// </summary>
[ApiController]
[Route("api/workspaces")]
public class WorkspaceController : ControllerBase
{
    private readonly ProjectScanner _scanner;
    private readonly ILogger<WorkspaceController> _logger;

    public WorkspaceController(ProjectScanner scanner, ILogger<WorkspaceController> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/workspaces/scan — scan a directory for project metadata.
    /// Path must be within the user's home directory.
    /// </summary>
    [HttpPost("scan")]
    public ActionResult<ProjectScanResult> ScanProject([FromBody] ScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { error = "Path is required" });

        if (!ValidatePath(request.Path, out var resolved, out var error))
            return BadRequest(new { error });

        try
        {
            var result = _scanner.ScanProject(resolved);
            return Ok(result);
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan project at '{Path}'", resolved);
            return Problem("Failed to scan project.");
        }
    }

    /// <summary>
    /// POST /api/workspaces/onboard — onboard a project (scan + metadata).
    /// Path must be within the user's home directory.
    /// </summary>
    [HttpPost("onboard")]
    public ActionResult<OnboardResult> OnboardProject([FromBody] ScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { error = "Path is required" });

        if (!ValidatePath(request.Path, out var resolved, out var error))
            return BadRequest(new { error });

        try
        {
            var scan = _scanner.ScanProject(resolved);

            return Ok(new OnboardResult(
                Scan: scan,
                Workspace: new WorkspaceMeta(
                    Path: scan.Path,
                    ProjectName: scan.ProjectName,
                    LastAccessedAt: DateTime.UtcNow
                )
            ));
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
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
/// Result of onboarding a project.
/// </summary>
public record OnboardResult(ProjectScanResult Scan, WorkspaceMeta Workspace);
