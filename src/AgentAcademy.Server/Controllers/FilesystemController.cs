using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Directory browser endpoint for workspace exploration.
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
    /// GET /api/filesystem/browse?path= — list directory contents.
    /// Defaults to the user's home directory. Restricted to home directory tree.
    /// </summary>
    [HttpGet("browse")]
    public IActionResult Browse([FromQuery] string? path)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var targetPath = string.IsNullOrWhiteSpace(path)
            ? homeDir
            : Path.GetFullPath(path);

        // Security: restrict browsing to home directory tree
        if (!targetPath.StartsWith(homeDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && targetPath != homeDir)
        {
            return BadRequest(new { error = "Path must be within the user's home directory" });
        }

        if (!Directory.Exists(targetPath))
            return NotFound(new { error = $"Directory not found: {targetPath}" });

        try
        {
            var entries = new List<object>();

            foreach (var dir in Directory.EnumerateDirectories(targetPath))
            {
                var name = Path.GetFileName(dir);
                // Skip hidden directories
                if (name.StartsWith('.'))
                    continue;

                entries.Add(new
                {
                    name,
                    type = "directory",
                    path = dir,
                });
            }

            foreach (var file in Directory.EnumerateFiles(targetPath))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.'))
                    continue;

                entries.Add(new
                {
                    name,
                    type = "file",
                    path = file,
                });
            }

            return Ok(new
            {
                path = targetPath,
                entries = entries.OrderBy(e => e.GetType().GetProperty("name")?.GetValue(e)?.ToString()),
            });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { error = $"Access denied: {targetPath}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to browse directory '{Path}'", targetPath);
            return Problem("Failed to browse directory.");
        }
    }
}
