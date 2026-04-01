using AgentAcademy.Server.Config;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
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
    private readonly IAgentExecutor _executor;

    public AuthController(
        GitHubAuthOptions authOptions,
        CopilotTokenProvider tokenProvider,
        IAgentExecutor executor)
    {
        _authOptions = authOptions;
        _tokenProvider = tokenProvider;
        _executor = executor;
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
            return Ok(new AuthStatusResult(
                AuthEnabled: false,
                Authenticated: false,
                CopilotStatus: CopilotStatusValues.Unavailable));
        }

        var cookieAuthenticated = User.Identity?.IsAuthenticated == true;
        var sdkOperational = cookieAuthenticated
            && !string.IsNullOrWhiteSpace(_tokenProvider.Token)
            && !_executor.IsAuthFailed;
        var copilotStatus = !cookieAuthenticated
            ? CopilotStatusValues.Unavailable
            : sdkOperational
                ? CopilotStatusValues.Operational
                : CopilotStatusValues.Degraded;
        var authenticated = cookieAuthenticated
            && string.Equals(copilotStatus, CopilotStatusValues.Operational, StringComparison.Ordinal);
        AuthUserInfo? user = null;

        if (cookieAuthenticated)
        {
            user = new AuthUserInfo(
                Login: User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
                Name: User.FindFirstValue("urn:github:name"),
                AvatarUrl: User.FindFirstValue("urn:github:avatar"));
        }

        return Ok(new AuthStatusResult(
            AuthEnabled: true,
            Authenticated: authenticated,
            CopilotStatus: copilotStatus,
            User: user));
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
