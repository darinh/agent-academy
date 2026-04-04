using AgentAcademy.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// REST endpoints for GitHub integration status.
/// </summary>
[ApiController]
[Route("api/github")]
public class GitHubController : ControllerBase
{
    private readonly IGitHubService _gitHubService;

    public GitHubController(IGitHubService gitHubService)
    {
        _gitHubService = gitHubService;
    }

    /// <summary>
    /// GET /api/github/status — returns GitHub integration status.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<GitHubStatusResponse>> GetStatus()
    {
        var isConfigured = await _gitHubService.IsConfiguredAsync();
        var repoSlug = isConfigured ? await _gitHubService.GetRepositorySlugAsync() : null;

        return Ok(new GitHubStatusResponse(isConfigured, repoSlug));
    }
}

public record GitHubStatusResponse(bool IsConfigured, string? Repository);
