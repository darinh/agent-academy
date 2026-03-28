using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// GitHub OAuth authentication endpoints.
/// All endpoints are anonymous — auth status is informational, login initiates the flow.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly GitHubAuthOptions _authOptions;
    private readonly CopilotTokenProvider _tokenProvider;

    public AuthController(
        GitHubAuthOptions authOptions,
        CopilotTokenProvider tokenProvider)
    {
        _authOptions = authOptions;
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// GET /api/auth/status — check authentication status.
    /// Returns whether auth is enabled and current user info if authenticated.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        if (!_authOptions.Enabled)
        {
            return Ok(new
            {
                authEnabled = false,
                authenticated = false,
            });
        }

        if (User.Identity?.IsAuthenticated != true)
        {
            return Ok(new
            {
                authEnabled = true,
                authenticated = false,
            });
        }

        return Ok(new
        {
            authEnabled = true,
            authenticated = true,
            user = new
            {
                login = User.FindFirstValue(ClaimTypes.Name),
                name = User.FindFirstValue("urn:github:name"),
                avatarUrl = User.FindFirstValue("urn:github:avatar"),
            }
        });
    }

    /// <summary>
    /// GET /api/auth/login — initiate GitHub OAuth flow.
    /// Redirects the browser to GitHub's authorization page.
    /// </summary>
    [HttpGet("login")]
    public IActionResult Login()
    {
        if (!_authOptions.Enabled)
            return BadRequest(new { error = "GitHub authentication is not configured." });

        // After OAuth completes on the backend, redirect to the frontend app
        var redirect = _authOptions.FrontendUrl.TrimEnd('/') + "/";

        return Challenge(
            new AuthenticationProperties { RedirectUri = redirect },
            "GitHub");
    }

    /// <summary>
    /// POST /api/auth/logout — sign out and clear the auth cookie.
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (User.Identity?.IsAuthenticated == true)
            _tokenProvider.ClearToken();
        return Ok(new { message = "Logged out." });
    }
}
