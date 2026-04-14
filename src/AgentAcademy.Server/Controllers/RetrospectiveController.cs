using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Read-only REST endpoints for retrospective history.
/// Retrospectives are TaskCommentEntity rows with CommentType == "Retrospective",
/// created by RetrospectiveService after task merge.
/// </summary>
[ApiController]
[Route("api/retrospectives")]
public sealed class RetrospectiveController : ControllerBase
{
    private static readonly string RetrospectiveType = nameof(TaskCommentType.Retrospective);
    private const int PreviewLength = 200;

    private readonly AgentAcademyDbContext _db;

    public RetrospectiveController(AgentAcademyDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/retrospectives — paginated retrospective list with optional agent and task filters.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? agentId = null,
        [FromQuery] string? taskId = null,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(ApiProblem.Unauthorized("Authentication is required.", "not_authenticated"));

        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);

        var query = _db.TaskComments.AsNoTracking()
            .Where(c => c.CommentType == RetrospectiveType);

        if (!string.IsNullOrWhiteSpace(agentId))
            query = query.Where(c => c.AgentId == agentId);

        if (!string.IsNullOrWhiteSpace(taskId))
            query = query.Where(c => c.TaskId == taskId);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Join(
                _db.Tasks.AsNoTracking(),
                c => c.TaskId,
                t => t.Id,
                (c, t) => new RetrospectiveListItem(
                    c.Id,
                    c.TaskId,
                    t.Title,
                    c.AgentId,
                    c.AgentName,
                    c.Content.Length > PreviewLength
                        ? c.Content.Substring(0, PreviewLength) + "…"
                        : c.Content,
                    c.CreatedAt))
            .ToListAsync(ct);

        return Ok(new RetrospectiveListResponse(items, total, limit, offset));
    }

    /// <summary>
    /// GET /api/retrospectives/{commentId} — single retrospective with current task metadata.
    /// Task fields reflect the current state of the task, not a historical snapshot.
    /// </summary>
    [HttpGet("{commentId}")]
    public async Task<IActionResult> Get(string commentId, CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(ApiProblem.Unauthorized("Authentication is required.", "not_authenticated"));

        var result = await _db.TaskComments.AsNoTracking()
            .Where(c => c.Id == commentId && c.CommentType == RetrospectiveType)
            .Join(
                _db.Tasks.AsNoTracking(),
                c => c.TaskId,
                t => t.Id,
                (c, t) => new RetrospectiveDetailResponse(
                    c.Id,
                    c.TaskId,
                    t.Title,
                    t.Status,
                    c.AgentId,
                    c.AgentName,
                    c.Content,
                    c.CreatedAt,
                    t.CompletedAt))
            .FirstOrDefaultAsync(ct);

        if (result is null)
            return NotFound(ApiProblem.NotFound($"Retrospective {commentId} not found.", "not_found"));

        return Ok(result);
    }

    /// <summary>
    /// GET /api/retrospectives/stats — aggregate retrospective statistics.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(ApiProblem.Unauthorized("Authentication is required.", "not_authenticated"));

        // Load lightweight projection for client-side aggregation
        // (SQLite provider can't translate GroupBy on anonymous types or string Length)
        var rows = await _db.TaskComments.AsNoTracking()
            .Where(c => c.CommentType == RetrospectiveType)
            .Select(c => new { c.AgentId, c.AgentName, ContentLength = c.Content.Length, c.CreatedAt })
            .ToListAsync(ct);

        var totalCount = rows.Count;

        var byAgent = rows
            .GroupBy(c => new { c.AgentId, c.AgentName })
            .Select(g => new RetrospectiveAgentStat(g.Key.AgentId, g.Key.AgentName, g.Count()))
            .OrderByDescending(a => a.Count)
            .ToList();

        var averageContentLength = totalCount > 0
            ? (int)Math.Round(rows.Average(c => (double)c.ContentLength))
            : 0;

        var latestAt = totalCount > 0
            ? rows.Max(c => (DateTime?)c.CreatedAt)
            : null;

        return Ok(new RetrospectiveStatsResponse(
            totalCount,
            byAgent,
            averageContentLength,
            latestAt));
    }
}

// --- DTOs ---

public sealed record RetrospectiveListItem(
    string Id,
    string TaskId,
    string TaskTitle,
    string AgentId,
    string AgentName,
    string ContentPreview,
    DateTime CreatedAt);

public sealed record RetrospectiveListResponse(
    List<RetrospectiveListItem> Retrospectives,
    int Total,
    int Limit,
    int Offset);

public sealed record RetrospectiveDetailResponse(
    string Id,
    string TaskId,
    string TaskTitle,
    string TaskStatus,
    string AgentId,
    string AgentName,
    string Content,
    DateTime CreatedAt,
    DateTime? TaskCompletedAt);

public sealed record RetrospectiveAgentStat(
    string AgentId,
    string AgentName,
    int Count);

public sealed record RetrospectiveStatsResponse(
    int TotalRetrospectives,
    List<RetrospectiveAgentStat> ByAgent,
    int AverageContentLength,
    DateTime? LatestRetrospectiveAt);
