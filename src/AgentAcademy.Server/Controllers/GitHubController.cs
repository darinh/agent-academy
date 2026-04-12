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
    private readonly CopilotTokenProvider _tokenProvider;

    public GitHubController(IGitHubService gitHubService, CopilotTokenProvider tokenProvider)
    {
        _gitHubService = gitHubService;
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// GET /api/github/status — returns GitHub integration status.
    /// AuthSource indicates how gh CLI is authenticated: "oauth" (from browser login),
    /// "cli" (from gh auth login), or "none".
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<GitHubStatusResponse>> GetStatus()
    {
        var hasOAuthToken = !string.IsNullOrWhiteSpace(_tokenProvider.Token);
        var isConfigured = await _gitHubService.IsConfiguredAsync();
        var repoSlug = isConfigured ? await _gitHubService.GetRepositorySlugAsync() : null;
        var authSource = isConfigured
            ? (hasOAuthToken ? "oauth" : "cli")
            : "none";

        return Ok(new GitHubStatusResponse(isConfigured, repoSlug, authSource));
    }
}

public record GitHubStatusResponse(bool IsConfigured, string? Repository, string AuthSource = "none");
