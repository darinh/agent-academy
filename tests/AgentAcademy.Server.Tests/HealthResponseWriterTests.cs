using System.Text.Json;
using AgentAcademy.Server.HealthChecks;
using AgentAcademy.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentAcademy.Server.Tests;

public sealed class HealthResponseWriterTests
{
    [Fact]
    public async Task WriteAsync_SetsContentTypeToJson()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var report = BuildReport(HealthStatus.Healthy);

        await HealthCheckResponseWriter.WriteAsync(context, report);

        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
    }

    [Fact]
    public async Task WriteAsync_SerializesStatus()
    {
        var (_, json) = await WriteAndParse(HealthStatus.Healthy);

        Assert.Equal("Healthy", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WriteAsync_IncludesTimestamp()
    {
        var (_, json) = await WriteAndParse(HealthStatus.Healthy);

        Assert.True(json.TryGetProperty("timestamp", out var ts));
        Assert.True(DateTime.TryParse(ts.GetString(), out _));
    }

    [Fact]
    public async Task WriteAsync_IncludesTotalDuration()
    {
        var (_, json) = await WriteAndParse(HealthStatus.Healthy);

        Assert.True(json.TryGetProperty("totalDuration", out var dur));
        Assert.True(dur.GetDouble() >= 0);
    }

    [Fact]
    public async Task WriteAsync_SerializesCheckEntries()
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["db"] = new(HealthStatus.Healthy, "ok", TimeSpan.FromMilliseconds(5), null, null),
            ["cache"] = new(HealthStatus.Degraded, "slow", TimeSpan.FromMilliseconds(200), null, null),
        };
        var report = new HealthReport(entries, TimeSpan.FromMilliseconds(210));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await HealthCheckResponseWriter.WriteAsync(context, report);

        context.Response.Body.Position = 0;
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        var checks = json.RootElement.GetProperty("checks");

        Assert.Equal(2, checks.GetArrayLength());
        Assert.Equal("db", checks[0].GetProperty("name").GetString());
        Assert.Equal("Healthy", checks[0].GetProperty("status").GetString());
        Assert.Equal("cache", checks[1].GetProperty("name").GetString());
        Assert.Equal("Degraded", checks[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task WriteAsync_ExcludesExceptionDetails()
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["db"] = new(HealthStatus.Unhealthy, "fail", TimeSpan.FromMilliseconds(1),
                new InvalidOperationException("secret connection string"), null),
        };
        var report = new HealthReport(entries, TimeSpan.FromMilliseconds(1));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await HealthCheckResponseWriter.WriteAsync(context, report);

        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("secret connection string", raw);
    }

    [Fact]
    public async Task WriteAsync_IncludesDataWhenPresent()
    {
        var data = new Dictionary<string, object> { ["version"] = "1.0" };
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["svc"] = new(HealthStatus.Healthy, "ok", TimeSpan.Zero, null, data),
        };
        var report = new HealthReport(entries, TimeSpan.Zero);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await HealthCheckResponseWriter.WriteAsync(context, report);

        context.Response.Body.Position = 0;
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        var check = json.RootElement.GetProperty("checks")[0];
        Assert.Equal("1.0", check.GetProperty("data").GetProperty("version").GetString());
    }

    [Fact]
    public async Task WriteAsync_OmitsDataWhenEmpty()
    {
        var (_, json) = await WriteAndParse(HealthStatus.Healthy);

        var check = json.GetProperty("checks")[0];
        Assert.False(check.TryGetProperty("data", out _));
    }

    private static HealthReport BuildReport(HealthStatus status)
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["test"] = new(status, "test check", TimeSpan.FromMilliseconds(1), null, null),
        };
        return new HealthReport(entries, TimeSpan.FromMilliseconds(1));
    }

    private static async Task<(DefaultHttpContext Context, JsonElement Json)> WriteAndParse(HealthStatus status)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var report = BuildReport(status);

        await HealthCheckResponseWriter.WriteAsync(context, report);

        context.Response.Body.Position = 0;
        var doc = await JsonDocument.ParseAsync(context.Response.Body);
        return (context, doc.RootElement);
    }
}

public sealed class SpaFallbackHelperTests
{
    [Theory]
    [InlineData("/dashboard", true)]
    [InlineData("/settings/profile", true)]
    [InlineData("/", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("/rooms/123", true)]
    public void ShouldServeIndex_ReturnsTrue_ForSpaRoutes(string? path, bool expected)
    {
        Assert.Equal(expected, SpaFallbackHelper.ShouldServeIndex(path));
    }

    [Theory]
    [InlineData("/api/rooms")]
    [InlineData("/api/agents/1")]
    [InlineData("/api")]
    [InlineData("/API/rooms")]
    [InlineData("/Api/Health")]
    public void ShouldServeIndex_ReturnsFalse_ForApiRoutes(string path)
    {
        Assert.False(SpaFallbackHelper.ShouldServeIndex(path));
    }

    [Theory]
    [InlineData("/hubs/activity")]
    [InlineData("/hubs")]
    [InlineData("/Hubs/Activity")]
    public void ShouldServeIndex_ReturnsFalse_ForHubRoutes(string path)
    {
        Assert.False(SpaFallbackHelper.ShouldServeIndex(path));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/Health")]
    [InlineData("/healthz")]
    public void ShouldServeIndex_ReturnsFalse_ForHealthRoutes(string path)
    {
        Assert.False(SpaFallbackHelper.ShouldServeIndex(path));
    }

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/Swagger/index.html")]
    public void ShouldServeIndex_ReturnsFalse_ForSwaggerRoutes(string path)
    {
        Assert.False(SpaFallbackHelper.ShouldServeIndex(path));
    }
}
