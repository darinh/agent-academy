using System.Text;
using System.Text.Json;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Downloadable analytics and conversation exports.
/// </summary>
[ApiController]
[Route("api/export")]
public class ExportController : ControllerBase
{
    private readonly IAgentAnalyticsService _analytics;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly IConversationExportService _conversationExport;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ExportController(
        IAgentAnalyticsService analytics,
        ILlmUsageTracker usageTracker,
        IConversationExportService conversationExport)
    {
        _analytics = analytics;
        _usageTracker = usageTracker;
        _conversationExport = conversationExport;
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
            return BadRequest(ApiProblem.BadRequest("hoursBack must be between 1 and 8760", "invalid_hours_back"));

        if (!IsValidFormat(format))
            return BadRequest(ApiProblem.BadRequest("format must be 'csv' or 'json'", "invalid_format"));

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
            return BadRequest(ApiProblem.BadRequest("hoursBack must be between 1 and 8760", "invalid_hours_back"));

        if (limit < 1 || limit > 50000)
            return BadRequest(ApiProblem.BadRequest("limit must be between 1 and 50000", "invalid_limit"));

        if (!IsValidFormat(format))
            return BadRequest(ApiProblem.BadRequest("format must be 'csv' or 'json'", "invalid_format"));

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

    // ── Conversation Exports ──────────────────────────────────────────────

    /// <summary>
    /// Export all messages in a room as JSON or Markdown.
    /// </summary>
    [HttpGet("rooms/{roomId}/messages")]
    public async Task<IActionResult> ExportRoomMessages(
        string roomId,
        [FromQuery] string format = "json",
        CancellationToken ct = default)
    {
        if (!IsValidConversationFormat(format))
            return BadRequest(ApiProblem.BadRequest("format must be 'json' or 'markdown'", "invalid_format"));

        var result = await _conversationExport.GetRoomMessagesForExportAsync(roomId, ct);
        if (result is null)
            return NotFound(ApiProblem.NotFound($"Room '{roomId}' not found.", "room_not_found"));

        var (room, messages, truncated) = result.Value;

        if (truncated)
            Response.Headers["X-Truncated"] = "true";
        Response.Headers["X-Record-Count"] = messages.Count.ToString();

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var safeName = SanitizeFilename(room.Name);

        if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            var md = ConversationExportService.FormatAsMarkdown(messages, roomName: room.Name);
            return FileResult(md, "text/markdown", $"room-{safeName}-{timestamp}.md");
        }

        var json = ConversationExportService.FormatAsJson(messages, roomName: room.Name);
        return FileResult(json, "application/json", $"room-{safeName}-{timestamp}.json");
    }

    /// <summary>
    /// Export all DM messages with a specific agent as JSON or Markdown.
    /// </summary>
    [HttpGet("dm/{agentId}/messages")]
    public async Task<IActionResult> ExportDmMessages(
        string agentId,
        [FromQuery] string format = "json",
        CancellationToken ct = default)
    {
        if (!IsValidConversationFormat(format))
            return BadRequest(ApiProblem.BadRequest("format must be 'json' or 'markdown'", "invalid_format"));

        var result = await _conversationExport.GetDmMessagesForExportAsync(agentId, ct);
        if (result is null)
            return NotFound(ApiProblem.NotFound($"No DM thread found for agent '{agentId}'.", "thread_not_found"));

        var (_, messages, truncated) = result.Value;

        if (truncated)
            Response.Headers["X-Truncated"] = "true";
        Response.Headers["X-Record-Count"] = messages.Count.ToString();

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var safeAgentId = SanitizeFilename(agentId);

        if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            var md = ConversationExportService.FormatAsMarkdown(messages, agentId: agentId);
            return FileResult(md, "text/markdown", $"dm-{safeAgentId}-{timestamp}.md");
        }

        var json = ConversationExportService.FormatAsJson(messages, agentId: agentId);
        return FileResult(json, "application/json", $"dm-{safeAgentId}-{timestamp}.json");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private FileContentResult FileResult(string content, string contentType, string filename)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return File(bytes, contentType + "; charset=utf-8", filename);
    }

    private static bool IsValidFormat(string format)
        => string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidConversationFormat(string format)
        => string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFilename(string name)
        => new string(name.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray())
            .ToLowerInvariant();
}
