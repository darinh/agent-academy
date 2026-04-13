using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Read-only REST endpoints for learning digest history.
/// Exposes digest listing, detail (with source retrospectives), and aggregate stats.
/// </summary>
[ApiController]
[Route("api/digests")]
public sealed class DigestController : ControllerBase
{
    private readonly AgentAcademyDbContext _db;

    public DigestController(AgentAcademyDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// GET /api/digests — paginated digest list with optional status filter.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status = null,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);

        var query = _db.LearningDigests.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = NormalizeStatus(status);
            if (normalized is null)
                return BadRequest(new { code = "invalid_status", message = "Status must be one of: Pending, Completed, Failed." });
            query = query.Where(d => d.Status == normalized);
        }

        var total = await query.CountAsync(ct);

        var digests = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(d => new DigestListItem(
                d.Id,
                d.CreatedAt,
                d.Summary,
                d.MemoriesCreated,
                d.RetrospectivesProcessed,
                d.Status))
            .ToListAsync(ct);

        return Ok(new DigestListResponse(digests, total, limit, offset));
    }

    /// <summary>
    /// GET /api/digests/{id} — single digest with source retrospective details.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        var digest = await _db.LearningDigests
            .AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new
            {
                d.Id,
                d.CreatedAt,
                d.Summary,
                d.MemoriesCreated,
                d.RetrospectivesProcessed,
                d.Status,
                Sources = d.Sources.Select(s => new DigestSourceItem(
                    s.RetrospectiveCommentId,
                    s.RetrospectiveComment != null ? s.RetrospectiveComment.TaskId : "",
                    s.RetrospectiveComment != null ? s.RetrospectiveComment.AgentId : "",
                    s.RetrospectiveComment != null ? s.RetrospectiveComment.Content : "",
                    s.RetrospectiveComment != null ? s.RetrospectiveComment.CreatedAt : default
                )).ToList()
            })
            .FirstOrDefaultAsync(ct);

        if (digest is null)
            return NotFound(new { code = "not_found", message = $"Digest {id} not found." });

        return Ok(new DigestDetailResponse(
            digest.Id,
            digest.CreatedAt,
            digest.Summary,
            digest.MemoriesCreated,
            digest.RetrospectivesProcessed,
            digest.Status,
            digest.Sources));
    }

    /// <summary>
    /// GET /api/digests/stats — aggregate digest statistics.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        var query = _db.LearningDigests.AsNoTracking();

        var byStatus = await query
            .GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var totalDigests = byStatus.Sum(s => s.Count);

        var completedQuery = query.Where(d => d.Status == "Completed");

        var totalMemories = await completedQuery.SumAsync(d => (int?)d.MemoriesCreated, ct) ?? 0;
        var totalRetrospectives = await completedQuery.SumAsync(d => (int?)d.RetrospectivesProcessed, ct) ?? 0;
        var lastCompleted = await completedQuery.MaxAsync(d => (DateTime?)d.CreatedAt, ct);

        var undigestedCount = await _db.Set<TaskCommentEntity>()
            .Where(c => c.CommentType == "Retrospective")
            .Where(c => !_db.LearningDigestSources
                .Where(s => s.Digest!.Status == "Completed")
                .Select(s => s.RetrospectiveCommentId)
                .Contains(c.Id))
            .CountAsync(ct);

        return Ok(new DigestStatsResponse(
            TotalDigests: totalDigests,
            ByStatus: byStatus.ToDictionary(x => x.Status, x => x.Count),
            TotalMemoriesCreated: totalMemories,
            TotalRetrospectivesProcessed: totalRetrospectives,
            UndigestedRetrospectives: undigestedCount,
            LastCompletedAt: lastCompleted));
    }

    private static string? NormalizeStatus(string status) => status switch
    {
        var s when s.Equals("Pending", StringComparison.OrdinalIgnoreCase) => "Pending",
        var s when s.Equals("Completed", StringComparison.OrdinalIgnoreCase) => "Completed",
        var s when s.Equals("Failed", StringComparison.OrdinalIgnoreCase) => "Failed",
        _ => null
    };
}

// --- DTOs ---

public sealed record DigestListItem(
    int Id,
    DateTime CreatedAt,
    string Summary,
    int MemoriesCreated,
    int RetrospectivesProcessed,
    string Status);

public sealed record DigestListResponse(
    List<DigestListItem> Digests,
    int Total,
    int Limit,
    int Offset);

public sealed record DigestSourceItem(
    string CommentId,
    string TaskId,
    string AgentId,
    string Content,
    DateTime CreatedAt);

public sealed record DigestDetailResponse(
    int Id,
    DateTime CreatedAt,
    string Summary,
    int MemoriesCreated,
    int RetrospectivesProcessed,
    string Status,
    List<DigestSourceItem> Sources);

public sealed record DigestStatsResponse(
    int TotalDigests,
    Dictionary<string, int> ByStatus,
    int TotalMemoriesCreated,
    int TotalRetrospectivesProcessed,
    int UndigestedRetrospectives,
    DateTime? LastCompletedAt);
