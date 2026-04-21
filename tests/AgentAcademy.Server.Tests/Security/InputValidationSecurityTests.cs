using System.Net;
using System.Text;
using System.Text.Json;
using AgentAcademy.Server.Tests.Fixtures;

namespace AgentAcademy.Server.Tests.Security;

/// <summary>
/// Integration tests for HTTP input validation on API endpoints.
/// Validates that oversized inputs, disallowed commands, and malformed payloads
/// are rejected with proper error responses.
/// </summary>
public sealed class InputValidationSecurityTests : IClassFixture<ApiContractFixture>
{
    private readonly HttpClient _client;

    public InputValidationSecurityTests(ApiContractFixture fixture)
    {
        _client = fixture.CreateClient();
    }

    private static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    // ── Command execution — validation and allowlist enforcement ─────
    // The test fixture runs with auth disabled (first-run public-access mode).
    // In this mode, the explicit User.Identity?.IsAuthenticated check in
    // CommandController is bypassed (fix: #59), so requests reach the
    // validation and allowlist logic. Only commands on the AllowedCommands
    // list are executable; everything else returns 403 Forbidden.

    [Fact]
    public async Task Execute_EmptyCommand_Returns400_ViaModelValidation()
    {
        // Model validation ([MinLength(1)]) fires before the method body,
        // so this returns 400 even without authentication.
        var response = await _client.PostAsync("/api/commands/execute",
            Json(new { command = "", args = new { } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Execute_NullPayload_Returns400_ViaBodyValidation()
    {
        // Null payload is rejected by the [Required] body binding before
        // the method body executes → 400.
        var response = await _client.PostAsync("/api/commands/execute",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Execute_DisallowedCommands_Return403_ViaAllowlist()
    {
        // Dangerous or non-human commands are blocked by the AllowedCommands
        // allowlist with a 403 Forbidden, regardless of auth state.
        var disallowed = new[] { "SHELL", "RUN_BUILD", "RESTART_SERVER" };

        foreach (var cmd in disallowed)
        {
            var response = await _client.PostAsync("/api/commands/execute",
                Json(new { command = cmd, args = new { } }));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // ── Filesystem browsing validation ─────────────────────────────

    [Fact]
    public async Task Browse_RelativePath_Returns400()
    {
        var response = await _client.GetAsync("/api/filesystem/browse?path=relative/path");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Browse_TraversalOutsideHome_Returns400()
    {
        var response = await _client.GetAsync("/api/filesystem/browse?path=/etc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Browse_RootPath_Returns400()
    {
        var response = await _client.GetAsync("/api/filesystem/browse?path=/");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Search validation ──────────────────────────────────────────

    [Fact]
    public async Task Search_EmptyQuery_Returns400()
    {
        var response = await _client.GetAsync("/api/search?q=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_InvalidScope_Returns400()
    {
        var response = await _client.GetAsync("/api/search?q=test&scope=drop_table");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_ExcessiveLimit_ClampedOrRejected()
    {
        // Server clamps limits to 1-100 range
        var response = await _client.GetAsync("/api/search?q=test&messageLimit=999");

        // Should either clamp silently (200) or reject (400) — never crash
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 200 or 400, got {(int)response.StatusCode}");
    }

    // ── JSON payload edge cases ────────────────────────────────────

    [Fact]
    public async Task Execute_MalformedJson_Returns400()
    {
        var response = await _client.PostAsync("/api/commands/execute",
            new StringContent("{invalid json}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Execute_NestedArrayInArgs_HandledGracefully()
    {
        // CommandController.NormalizeArgs should handle or reject non-scalar arg values
        var payload = new
        {
            command = "LIST_TASKS",
            args = new { nested = new[] { "a", "b", "c" } }
        };

        var response = await _client.PostAsync("/api/commands/execute", Json(payload));

        // Should either succeed (with stringified arg) or return 400 — never 500
        Assert.True(
            (int)response.StatusCode < 500,
            $"Nested array arg caused server error: {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Execute_DeeplyNestedObjectInArgs_HandledGracefully()
    {
        var payload = new
        {
            command = "LIST_TASKS",
            args = new { deep = new { level1 = new { level2 = new { level3 = "value" } } } }
        };

        var response = await _client.PostAsync("/api/commands/execute", Json(payload));

        Assert.True(
            (int)response.StatusCode < 500,
            $"Deeply nested arg caused server error: {(int)response.StatusCode}");
    }

    // ── Health endpoints (AllowAnonymous verification) ──────────────

    [Fact]
    public async Task HealthEndpoint_AlwaysAccessible()
    {
        var response = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InstanceHealth_AlwaysAccessible()
    {
        var response = await _client.GetAsync("/api/health/instance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
