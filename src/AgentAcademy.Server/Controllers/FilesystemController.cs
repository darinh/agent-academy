using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Directory browser endpoint for workspace exploration.
/// Ported from v1: only returns directories, supports showHidden param,
/// returns permissionDenied on EACCES instead of throwing.
/// </summary>
[ApiController]
[Route("api/filesystem")]
public class FilesystemController : ControllerBase
{
    private readonly ILogger<FilesystemController> _logger;

    public FilesystemController(ILogger<FilesystemController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// GET /api/filesystem/browse?path=&amp;showHidden=true — list subdirectories.
    /// Defaults to the user's home directory. Only returns directories.
    /// </summary>
    [HttpGet("browse")]
    public IActionResult Browse(
        [FromQuery] string? path,
        [FromQuery] string? showHidden)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var targetPath = string.IsNullOrWhiteSpace(path)
            ? homeDir
            : path;

        // Must be absolute
        if (!Path.IsPathRooted(targetPath))
            return BadRequest(ApiProblem.BadRequest("Path must be absolute", "invalid_path"));

        var resolved = Path.GetFullPath(targetPath);

        // Security: restrict browsing to home directory tree
        if (!resolved.StartsWith(homeDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && resolved != homeDir)
        {
            return BadRequest(ApiProblem.BadRequest("Path must be within the user's home directory", "invalid_path"));
        }

        if (!Directory.Exists(resolved))
            return NotFound(ApiProblem.NotFound($"Directory not found: {resolved}", "not_found"));

        var includeHidden = string.Equals(showHidden, "true", StringComparison.Ordinal);

        try
        {
            var entries = new List<object>();

            foreach (var dir in Directory.EnumerateDirectories(resolved))
            {
                var name = Path.GetFileName(dir);

                // Skip hidden directories unless showHidden=true
                if (!includeHidden && name.StartsWith('.'))
                    continue;

                entries.Add(new
                {
                    name,
                    path = dir,
                    isDirectory = true,
                });
            }

            // Compute parent (null if at root)
            var parent = Path.GetDirectoryName(resolved);

            return Ok(new
            {
                current = resolved,
                parent,
                entries = entries
                    .OrderBy(e => ((dynamic)e).name as string)
                    .ToList(),
            });
        }
        catch (UnauthorizedAccessException)
        {
            // Match v1 behavior: return permissionDenied flag instead of error
            return Ok(new
            {
                current = resolved,
                parent = Path.GetDirectoryName(resolved),
                entries = Array.Empty<object>(),
                permissionDenied = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to browse directory '{Path}'", resolved);
            return Problem("Failed to browse directory.");
        }
    }
}
