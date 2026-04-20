using System.Text.Json;
using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="CsrfProtectionMiddleware"/> — the middleware
/// that blocks cross-origin form POSTs against cookie-authenticated users
/// (issue #80).
/// </summary>
public sealed class CsrfProtectionMiddlewareTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task SafeMethod_WithoutHeader_PassesThrough(string method)
    {
        var (context, nextCalled) = BuildContext(method);
        context.Request.Cookies = BuildCookies((AuthenticationExtensions.AuthCookieName, "signed-in"));

        await InvokeAsync(context, nextCalled);

        Assert.True(nextCalled.Value);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Post_WithAuthCookie_WithHeader_PassesThrough()
    {
        var (context, nextCalled) = BuildContext("POST");
        context.Request.Cookies = BuildCookies((AuthenticationExtensions.AuthCookieName, "signed-in"));
        context.Request.Headers[CsrfProtectionMiddleware.RequiredHeaderName] = "XMLHttpRequest";

        await InvokeAsync(context, nextCalled);

        Assert.True(nextCalled.Value);
    }

    [Fact]
    public async Task Post_WithAuthCookie_WithoutHeader_IsRejected()
    {
        var (context, nextCalled) = BuildContext("POST");
        context.Request.Cookies = BuildCookies((AuthenticationExtensions.AuthCookieName, "signed-in"));

        await InvokeAsync(context, nextCalled);

        Assert.False(nextCalled.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        var problem = await ReadProblemAsync(context);
        Assert.Equal("CSRF_HEADER_REQUIRED", problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task MutatingMethods_WithAuthCookie_WithoutHeader_AreRejected(string method)
    {
        var (context, nextCalled) = BuildContext(method);
        context.Request.Cookies = BuildCookies((AuthenticationExtensions.AuthCookieName, "signed-in"));

        await InvokeAsync(context, nextCalled);

        Assert.False(nextCalled.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Post_WithConsultantKey_PassesThrough()
    {
        // Consultant API callers never present the auth cookie; the consultant
        // key header is itself a bearer credential with no browser CSRF surface.
        var (context, nextCalled) = BuildContext("POST");
        context.Request.Headers[ConsultantKeyAuthHandler.HeaderName] = "secret-key";

        await InvokeAsync(context, nextCalled);

        Assert.True(nextCalled.Value);
    }

    [Fact]
    public async Task Post_WithConsultantKey_AndAuthCookie_PassesThrough()
    {
        // Defensive: if a request somehow carries both, the consultant-key path
        // wins because the middleware exempts any request carrying that header.
        var (context, nextCalled) = BuildContext("POST");
        context.Request.Headers[ConsultantKeyAuthHandler.HeaderName] = "secret-key";
        context.Request.Cookies = BuildCookies((AuthenticationExtensions.AuthCookieName, "signed-in"));

        await InvokeAsync(context, nextCalled);

        Assert.True(nextCalled.Value);
    }

    [Fact]
    public async Task Post_Anonymous_PassesThrough()
    {
        // Unauthenticated mutating requests aren't the target of this defense.
        // The downstream authorization layer handles them.
        var (context, nextCalled) = BuildContext("POST");

        await InvokeAsync(context, nextCalled);

        Assert.True(nextCalled.Value);
    }

    [Fact]
    public async Task Post_WithUnrelatedCookies_PassesThrough()
    {
        // A request that happens to carry some other cookie (e.g. analytics)
        // but not the auth cookie must not be rejected.
        var (context, nextCalled) = BuildContext("POST");
        context.Request.Cookies = BuildCookies(("some-tracker", "abc"));

        await InvokeAsync(context, nextCalled);

        Assert.True(nextCalled.Value);
    }

    [Fact]
    public async Task Post_WithAuthCookie_HeaderValueDoesNotMatter()
    {
        // CORS preflight is triggered by ANY non-safelisted header; the server
        // does not need to validate the value, only its presence.
        var (context, nextCalled) = BuildContext("POST");
        context.Request.Cookies = BuildCookies((AuthenticationExtensions.AuthCookieName, "signed-in"));
        context.Request.Headers[CsrfProtectionMiddleware.RequiredHeaderName] = "fetch";

        await InvokeAsync(context, nextCalled);

        Assert.True(nextCalled.Value);
    }

    [Fact]
    public async Task Post_SignalRNegotiate_WithAuthCookie_WithoutHeader_IsRejected()
    {
        // Regression: SignalR's negotiate POST was blocked by CSRF because the
        // JS client did not send X-Requested-With. The client-side fix adds the
        // header, but this test proves the middleware IS protecting the path.
        var (context, nextCalled) = BuildContext("POST");
        context.Request.Path = "/hubs/activity/negotiate";
        context.Request.Cookies = BuildCookies((AuthenticationExtensions.AuthCookieName, "signed-in"));

        await InvokeAsync(context, nextCalled);

        Assert.False(nextCalled.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Post_SignalRNegotiate_WithAuthCookie_WithHeader_PassesThrough()
    {
        // After the client-side fix: negotiate POST with X-Requested-With passes.
        var (context, nextCalled) = BuildContext("POST");
        context.Request.Path = "/hubs/activity/negotiate";
        context.Request.Cookies = BuildCookies((AuthenticationExtensions.AuthCookieName, "signed-in"));
        context.Request.Headers[CsrfProtectionMiddleware.RequiredHeaderName] = "XMLHttpRequest";

        await InvokeAsync(context, nextCalled);

        Assert.True(nextCalled.Value);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static async Task InvokeAsync(HttpContext context, StrongBox<bool> nextCalled)
    {
        var middleware = new CsrfProtectionMiddleware(
            next: ctx => { nextCalled.Value = true; return Task.CompletedTask; },
            logger: NullLogger<CsrfProtectionMiddleware>.Instance);
        await middleware.InvokeAsync(context);
    }

    private static (HttpContext context, StrongBox<bool> nextCalled) BuildContext(string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = "/api/example";
        context.Response.Body = new MemoryStream();
        return (context, new StrongBox<bool>(false));
    }

    private static IRequestCookieCollection BuildCookies(params (string Name, string Value)[] entries)
    {
        var dict = entries.ToDictionary(e => e.Name, e => e.Value);
        return new StubCookieCollection(dict);
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        return doc.RootElement.Clone();
    }

    private sealed class StrongBox<T>
    {
        public T Value;
        public StrongBox(T initial) { Value = initial; }
    }

    private sealed class StubCookieCollection : IRequestCookieCollection
    {
        private readonly IDictionary<string, string> _inner;
        public StubCookieCollection(IDictionary<string, string> inner) { _inner = inner; }
        public string? this[string key] => _inner.TryGetValue(key, out var v) ? v : null;
        public int Count => _inner.Count;
        public ICollection<string> Keys => _inner.Keys;
        public bool ContainsKey(string key) => _inner.ContainsKey(key);
        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value)
        {
            var ok = _inner.TryGetValue(key, out var v);
            value = v!;
            return ok;
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
