using System.Net;
using System.Text.Json;
using AgentAcademy.Server.Tests.Fixtures;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// API contract tests that validate the OpenAPI spec is generated correctly
/// and that real HTTP responses match the documented contract.
/// </summary>
public sealed class OpenApiContractTests : IClassFixture<ApiContractFixture>
{
    private readonly HttpClient _client;

    public OpenApiContractTests(ApiContractFixture fixture)
    {
        _client = fixture.CreateClient();
    }

    // ── Swagger doc generation ──────────────────────────────────────────────

    [Fact]
    public async Task SwaggerEndpoint_Returns200WithValidJson()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }

    [Fact]
    public async Task SwaggerDoc_ContainsOpenApi3Version()
    {
        var doc = await GetSwaggerDocAsync();

        Assert.True(doc.RootElement.TryGetProperty("openapi", out var version));
        Assert.StartsWith("3.0", version.GetString());
    }

    [Fact]
    public async Task SwaggerDoc_ContainsInfoSection()
    {
        var doc = await GetSwaggerDocAsync();

        Assert.True(doc.RootElement.TryGetProperty("info", out var info));
        Assert.True(info.TryGetProperty("title", out _));
        Assert.True(info.TryGetProperty("version", out _));
    }

    // ── Route coverage ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/rooms")]
    [InlineData("/api/agents")]
    [InlineData("/api/workspaces")]
    [InlineData("/api/commands")]
    [InlineData("/api/settings")]
    [InlineData("/api/sessions")]
    [InlineData("/api/analytics")]
    [InlineData("/api/activity")]
    [InlineData("/api/sprints")]
    [InlineData("/api/search")]
    [InlineData("/api/worktrees")]
    [InlineData("/api/notifications")]
    [InlineData("/api/memories")]
    [InlineData("/api/retrospectives")]
    [InlineData("/api/digests")]
    [InlineData("/api/export")]
    [InlineData("/api/auth")]
    [InlineData("/api/github")]
    [InlineData("/api/filesystem")]
    [InlineData("/api/instruction-templates")]
    [InlineData("/api/commands/audit")]
    public async Task SwaggerDoc_ContainsExpectedPath(string expectedPath)
    {
        var doc = await GetSwaggerDocAsync();
        var paths = doc.RootElement.GetProperty("paths");

        // Controllers may register sub-paths; verify at least one path starts with the expected prefix
        var found = false;
        foreach (var path in paths.EnumerateObject())
        {
            if (path.Name.StartsWith(expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }

        Assert.True(found, $"No path starting with '{expectedPath}' found in OpenAPI spec. " +
            $"Available paths: {string.Join(", ", paths.EnumerateObject().Select(p => p.Name).Take(10))}...");
    }

    [Fact]
    public async Task SwaggerDoc_HasReasonablePathCount()
    {
        var doc = await GetSwaggerDocAsync();
        var paths = doc.RootElement.GetProperty("paths");
        var count = paths.EnumerateObject().Count();

        // We know there are ~145 endpoints across 26 controllers.
        // The path count should be in the right ballpark.
        Assert.True(count >= 50, $"Expected at least 50 paths, got {count}");
    }

    [Fact]
    public async Task SwaggerDoc_AllPathsHaveAtLeastOneOperation()
    {
        var doc = await GetSwaggerDocAsync();
        var paths = doc.RootElement.GetProperty("paths");
        var httpMethods = new[] { "get", "post", "put", "delete", "patch", "head", "options" };

        foreach (var path in paths.EnumerateObject())
        {
            var hasOperation = path.Value.EnumerateObject()
                .Any(prop => httpMethods.Contains(prop.Name.ToLowerInvariant()));
            Assert.True(hasOperation, $"Path '{path.Name}' has no HTTP operations defined");
        }
    }

    // ── Response contract matching ──────────────────────────────────────────

    [Fact]
    public async Task GetRooms_Returns200WithJsonArray()
    {
        var response = await _client.GetAsync("/api/rooms");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task GetAgentLocations_Returns200WithJsonArray()
    {
        var response = await _client.GetAsync("/api/agents/locations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task GetSessions_Returns200WithJson()
    {
        var response = await _client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(
            doc.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object,
            $"Expected Array or Object, got {doc.RootElement.ValueKind}");
    }

    [Fact]
    public async Task GetNonexistentRoom_Returns404()
    {
        var response = await _client.GetAsync("/api/rooms/nonexistent-id-999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSettings_Returns200WithJsonObject()
    {
        var response = await _client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task SwaggerDoc_OperationsHaveResponses()
    {
        var doc = await GetSwaggerDocAsync();
        var paths = doc.RootElement.GetProperty("paths");
        var httpMethods = new[] { "get", "post", "put", "delete", "patch" };
        var violations = new List<string>();

        foreach (var path in paths.EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject()
                .Where(p => httpMethods.Contains(p.Name.ToLowerInvariant())))
            {
                if (!op.Value.TryGetProperty("responses", out var responses) ||
                    responses.EnumerateObject().Count() == 0)
                {
                    violations.Add($"{op.Name.ToUpperInvariant()} {path.Name}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Operations missing response definitions: {string.Join(", ", violations)}");
    }

    // ── Schema completeness ─────────────────────────────────────────────────

    [Fact]
    public async Task SwaggerDoc_HasComponentsSchemas()
    {
        var doc = await GetSwaggerDocAsync();

        Assert.True(doc.RootElement.TryGetProperty("components", out var components));
        Assert.True(components.TryGetProperty("schemas", out var schemas));
        Assert.True(schemas.EnumerateObject().Count() > 0, "No schemas defined in components");
    }

    [Fact]
    public async Task SwaggerDoc_ContainsKeyDomainSchemas()
    {
        var doc = await GetSwaggerDocAsync();
        var schemas = doc.RootElement
            .GetProperty("components")
            .GetProperty("schemas");

        var schemaNames = schemas.EnumerateObject()
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Key domain types that should appear in the OpenAPI spec
        var expectedSchemas = new[]
        {
            "RoomSnapshot",
            "AgentDefinition",
            "ChatEnvelope",
        };

        foreach (var expected in expectedSchemas)
        {
            Assert.True(schemaNames.Contains(expected),
                $"Expected schema '{expected}' not found. Available: {string.Join(", ", schemaNames.Take(20))}...");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<JsonDocument> GetSwaggerDocAsync()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
