using System.Text.Json;
using AgentAcademy.Server.Auth;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Middleware;

/// <summary>
/// Blocks cross-site request forgery against cookie-authenticated browser
/// requests by requiring a custom header on every mutating request that
/// carries the auth cookie.
/// </summary>
/// <remarks>
/// <para>
/// The production auth cookie is <c>SameSite=None; Secure</c> (spec 015 §2.1)
/// so browsers will attach it to cross-origin requests. CORS with
/// <c>AllowCredentials</c> + an explicit origin allowlist blocks
/// <c>fetch(..., { credentials: "include" })</c> attacks from unlisted
/// origins, but it does NOT block simple HTML form POSTs, which browsers
/// send cross-origin by default and which can trivially include cookies.
/// </para>
/// <para>
/// Defense chosen (see issue #80, option 2): require an
/// <c>X-Requested-With</c> header on all mutating requests that carry the
/// auth cookie. Setting a non-CORS-safelisted request header forces the
/// browser to issue a CORS preflight, which the existing origin allowlist
/// then enforces. A cross-origin HTML form cannot set custom headers, so
/// the forged request is rejected here with 403.
/// </para>
/// <para>
/// Consultant API callers (<c>X-Consultant-Key</c>) are unaffected: they
/// never present the auth cookie, so this middleware short-circuits for
/// them. Anonymous requests are likewise unaffected — the downstream
/// authorization pipeline handles them.
/// </para>
/// </remarks>
public sealed class CsrfProtectionMiddleware
{
    public const string RequiredHeaderName = "X-Requested-With";

    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS", "TRACE",
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<CsrfProtectionMiddleware> _logger;

    public CsrfProtectionMiddleware(RequestDelegate next, ILogger<CsrfProtectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (ShouldReject(context.Request))
        {
            _logger.LogWarning(
                "Blocked mutating request to {Path} ({Method}) from {RemoteIp}: auth cookie present but {Header} header missing.",
                context.Request.Path,
                context.Request.Method,
                context.Connection.RemoteIpAddress,
                RequiredHeaderName);

            await WriteForbiddenAsync(context);
            return;
        }

        await _next(context);
    }

    private static bool ShouldReject(HttpRequest request)
    {
        if (SafeMethods.Contains(request.Method))
            return false;

        // Consultant API callers authenticate via a custom header, not the
        // auth cookie, so they are never at risk of CSRF from a browser.
        if (request.Headers.ContainsKey(ConsultantKeyAuthHandler.HeaderName))
            return false;

        // Only cookie-carrying requests are in scope. Anonymous mutating
        // requests have no session to hijack; the authorization layer
        // rejects them on its own when the endpoint requires auth.
        if (!request.Cookies.ContainsKey(AuthenticationExtensions.AuthCookieName))
            return false;

        return !request.Headers.ContainsKey(RequiredHeaderName);
    }

    private static async Task WriteForbiddenAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
            Title = "CSRF protection: missing required header.",
            Status = StatusCodes.Status403Forbidden,
            Detail =
                $"Mutating requests that carry the auth cookie must include an " +
                $"'{RequiredHeaderName}' header. See spec 015 §2.5.",
        };
        problem.Extensions["code"] = "CSRF_HEADER_REQUIRED";

        await JsonSerializer.SerializeAsync(context.Response.Body, problem);
    }
}
