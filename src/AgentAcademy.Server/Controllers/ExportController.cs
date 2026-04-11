using System.Text;
using System.Text.Json;
using AgentAcademy.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Downloadable analytics exports in CSV or JSON format.
/// </summary>
[ApiController]
[Route("api/export")]
public class ExportController : ControllerBase
{
    private readonly AgentAnalyticsService _analytics;
    private readonly LlmUsageTracker _usageTracker;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ExportController(
        AgentAnalyticsService analytics,
        LlmUsageTracker usageTracker)
    {
        _analytics = analytics;
        _usageTracker = usageTracker;
    }

    /// <summary>
    /// Export agent performance summary — one row per agent.
    /// </summary>
    [HttpGet("agents")]
    public async Task<IActionResult> ExportAgents(
        [FromQuery] int? hoursBack = null,
        [FromQuery] string format = "csv",
        CancellationToken ct = default)
    {
        if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
            return BadRequest(new { code = "invalid_hours_back", message = "hoursBack must be between 1 and 8760" });

        if (!IsValidFormat(format))
            return BadRequest(new { code = "invalid_format", message = "format must be 'csv' or 'json'" });

        var summary = await _analytics.GetAnalyticsSummaryAsync(hoursBack, ct);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(summary, JsonOptions);
            return FileResult(json, "application/json", $"agent-analytics-{timestamp}.json");
        }

        var csv = CsvExportService.AgentSummaryToCsv(summary);
        return FileResult(csv, "text/csv", $"agent-analytics-{timestamp}.csv");
    }

    /// <summary>
    /// Export raw LLM usage records. Supports optional agentId filter.
    /// </summary>
    [HttpGet("usage")]
    public async Task<IActionResult> ExportUsage(
        [FromQuery] int? hoursBack = null,
        [FromQuery] string? agentId = null,
        [FromQuery] int limit = 10000,
        [FromQuery] string format = "csv",
        CancellationToken ct = default)
    {
        if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
            return BadRequest(new { code = "invalid_hours_back", message = "hoursBack must be between 1 and 8760" });

        if (limit < 1 || limit > 50000)
            return BadRequest(new { code = "invalid_limit", message = "limit must be between 1 and 50000" });

        if (!IsValidFormat(format))
            return BadRequest(new { code = "invalid_format", message = "format must be 'csv' or 'json'" });

        var since = hoursBack.HasValue
            ? DateTime.UtcNow.AddHours(-hoursBack.Value)
            : (DateTime?)null;

        var records = await _usageTracker.GetRecentUsageAsync(
            roomId: null, agentId: agentId, limit: limit + 1, since: since, ct: ct);

        var truncated = records.Count > limit;
        if (truncated)
        {
            records = records.Take(limit).ToList();
            Response.Headers["X-Truncated"] = "true";
        }
        Response.Headers["X-Record-Count"] = records.Count.ToString();

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var agentSuffix = agentId is not null ? $"-{agentId}" : "";

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(records, JsonOptions);
            return FileResult(json, "application/json", $"usage-records{agentSuffix}-{timestamp}.json");
        }

        var csv = CsvExportService.UsageRecordsToCsv(records);
        return FileResult(csv, "text/csv", $"usage-records{agentSuffix}-{timestamp}.csv");
    }

    private FileContentResult FileResult(string content, string contentType, string filename)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return File(bytes, contentType + "; charset=utf-8", filename);
    }

    private static bool IsValidFormat(string format)
        => string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
}
