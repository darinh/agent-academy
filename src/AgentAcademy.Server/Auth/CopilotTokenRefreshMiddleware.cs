using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AgentAcademy.Server.Auth;

/// <summary>
/// Restores Copilot SDK tokens from the auth cookie on the first authenticated
/// request after a server restart, and writes refreshed tokens back to the cookie
/// so they survive future restarts.
/// Scoped to cookie-authenticated requests only (consultant-key auth is stateless).
/// </summary>
public sealed class CopilotTokenRefreshMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ICopilotTokenProvider _tokenProvider;
    private readonly ILogger<CopilotTokenRefreshMiddleware> _logger;

    public CopilotTokenRefreshMiddleware(
        RequestDelegate next,
        ICopilotTokenProvider tokenProvider,
        ILogger<CopilotTokenRefreshMiddleware> logger)
    {
        _next = next;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only process cookie-authenticated requests (not consultant-key auth)
        var cookieResult = await context.AuthenticateAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);

        if (cookieResult.Succeeded)
        {
            await TryRestoreTokenFromCookieAsync(context);
            await TryWriteRefreshedTokensToCookieAsync(context, cookieResult);
        }

        await _next(context);
    }

    /// <summary>
    /// On the first cookie-authenticated request after a restart, restore the
    /// OAuth token from the auth cookie so the Copilot SDK can resume.
    /// </summary>
    private async Task TryRestoreTokenFromCookieAsync(HttpContext context)
    {
        if (_tokenProvider.Token is not null)
            return;

        var accessToken = await context.GetTokenAsync("access_token");
        var refreshToken = await context.GetTokenAsync("refresh_token");
        var expiresAtStr = await context.GetTokenAsync("expires_at");

        if (string.IsNullOrEmpty(accessToken))
            return;

        TimeSpan? expiresIn = null;
        if (DateTimeOffset.TryParse(expiresAtStr, out var expiresAt))
        {
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
                expiresIn = remaining;
        }

        _tokenProvider.SetTokens(accessToken, refreshToken, expiresIn);
    }

    /// <summary>
    /// When the token provider has refreshed tokens, write them back to the
    /// auth cookie so they persist across server restarts.
    /// </summary>
    private async Task TryWriteRefreshedTokensToCookieAsync(
        HttpContext context,
        AuthenticateResult authenticateResult)
    {
        if (!_tokenProvider.HasPendingCookieUpdate)
            return;

        if (!authenticateResult.Succeeded || authenticateResult.Properties is null)
            return;

        try
        {
            // Merge with existing tokens to avoid clobbering token_type, scope, etc.
            var existingTokens = authenticateResult.Properties.GetTokens()
                .Where(t => t.Name is not ("access_token" or "refresh_token" or "expires_at"))
                .ToList();
            existingTokens.Add(new AuthenticationToken { Name = "access_token", Value = _tokenProvider.Token ?? "" });
            existingTokens.Add(new AuthenticationToken { Name = "refresh_token", Value = _tokenProvider.RefreshToken ?? "" });
            existingTokens.Add(new AuthenticationToken { Name = "expires_at", Value = _tokenProvider.ExpiresAtUtc?.ToString("o") ?? "" });
            authenticateResult.Properties.StoreTokens(existingTokens);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                authenticateResult.Principal!,
                authenticateResult.Properties);
            _tokenProvider.ClearCookieUpdatePending();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to write refreshed tokens to auth cookie — will retry on next request");
        }
    }
}

/// <summary>
/// Extension method to register the token refresh middleware.
/// Should be called after <c>UseAuthentication()</c> and before endpoint routing.
/// </summary>
public static class CopilotTokenRefreshMiddlewareExtensions
{
    public static IApplicationBuilder UseCopilotTokenRefresh(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CopilotTokenRefreshMiddleware>();
    }
}
