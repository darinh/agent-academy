using System.ComponentModel.DataAnnotations;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// REST endpoints for bulk memory operations — export and import.
/// Complements the EXPORT_MEMORIES and IMPORT_MEMORIES agent commands
/// with direct HTTP access for the human operator.
/// </summary>
[ApiController]
[Route("api/memories")]
public class MemoryController : ControllerBase
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<MemoryController> _logger;

    /// <summary>Max entries per import request to prevent payload abuse.</summary>
    internal const int MaxImportEntries = 500;

    /// <summary>Max error messages returned in import response.</summary>
    private const int MaxReportedErrors = 50;

    public MemoryController(AgentAcademyDbContext db, ILogger<MemoryController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/memories/export?agentId=X&amp;category=Y — export memories as JSON.
    /// agentId is required to prevent accidental full-corpus dumps.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? agentId, [FromQuery] string? category)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        if (string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { code = "missing_agent_id", message = "agentId query parameter is required." });

        var query = _db.AgentMemories.Where(m => m.AgentId == agentId);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(m => m.Category == category.ToLowerInvariant());

        var memories = await query.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();

        var exported = memories.Select(m => new MemoryExportDto
        {
            AgentId = m.AgentId,
            Category = m.Category,
            Key = m.Key,
            Value = m.Value,
            CreatedAt = m.CreatedAt,
            UpdatedAt = m.UpdatedAt,
            LastAccessedAt = m.LastAccessedAt,
            ExpiresAt = m.ExpiresAt,
        }).ToList();

        return Ok(new { count = exported.Count, memories = exported });
    }

    /// <summary>
    /// POST /api/memories/import — bulk import memories from JSON.
    /// Each entry requires agentId, category, key, and value.
    /// Uses upsert semantics (existing keys are updated).
    /// Capped at 500 entries per request.
    /// </summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] MemoryImportRequest? request)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        if (request?.Memories is null || request.Memories.Count == 0)
            return BadRequest(new { code = "invalid_request", message = "Request must contain a non-empty 'memories' array." });

        if (request.Memories.Count > MaxImportEntries)
            return BadRequest(new { code = "payload_too_large", message = $"Import is capped at {MaxImportEntries} entries per request. Got {request.Memories.Count}." });

        var now = DateTime.UtcNow;
        int created = 0, updated = 0, skipped = 0;
        var errors = new List<string>();

        foreach (var entry in request.Memories)
        {
            if (string.IsNullOrWhiteSpace(entry.AgentId) || string.IsNullOrWhiteSpace(entry.Key) ||
                string.IsNullOrWhiteSpace(entry.Value) || string.IsNullOrWhiteSpace(entry.Category))
            {
                skipped++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add("Skipped entry with missing agentId/key/value/category");
                continue;
            }

            var category = entry.Category.ToLowerInvariant();
            if (!RememberHandler.ValidCategories.Contains(category))
            {
                skipped++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add($"Skipped '{entry.Key}': invalid category '{entry.Category}'");
                continue;
            }

            if (entry.Value.Length > 500)
            {
                skipped++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add($"Skipped '{entry.Key}': value exceeds 500 character limit");
                continue;
            }

            if (entry.TtlHours.HasValue && (entry.TtlHours.Value <= 0 || entry.TtlHours.Value > 87600))
            {
                skipped++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add($"Skipped '{entry.Key}': ttlHours must be 1-87600");
                continue;
            }

            var existing = await _db.AgentMemories.FindAsync(entry.AgentId, entry.Key);
            var expiresAt = entry.TtlHours is > 0 ? (DateTime?)now.AddHours(entry.TtlHours.Value) : null;
            if (existing != null)
            {
                existing.Category = category;
                existing.Value = entry.Value;
                existing.UpdatedAt = now;
                if (entry.TtlHours.HasValue)
                    existing.ExpiresAt = expiresAt;
                updated++;
            }
            else
            {
                _db.AgentMemories.Add(new AgentMemoryEntity
                {
                    AgentId = entry.AgentId,
                    Key = entry.Key,
                    Category = category,
                    Value = entry.Value,
                    CreatedAt = now,
                    ExpiresAt = expiresAt,
                });
                created++;
            }
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("Memory import: {Created} created, {Updated} updated, {Skipped} skipped out of {Total}",
            created, updated, skipped, request.Memories.Count);

        return Ok(new { created, updated, skipped, total = request.Memories.Count, errors = errors.Count > 0 ? errors : null });
    }

    /// <summary>
    /// DELETE /api/memories/expired?agentId=X — removes expired memories for a specific agent.
    /// agentId is required to prevent accidental cross-agent bulk deletes.
    /// </summary>
    [HttpDelete("expired")]
    public async Task<IActionResult> CleanupExpired([FromQuery] string? agentId)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        if (string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { code = "missing_agent_id", message = "agentId query parameter is required." });

        var now = DateTime.UtcNow;
        var expired = await _db.AgentMemories
            .Where(m => m.AgentId == agentId && m.ExpiresAt != null && m.ExpiresAt <= now)
            .ToListAsync();

        _db.AgentMemories.RemoveRange(expired);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Memory cleanup: {Count} expired memories removed for agent {AgentId}",
            expired.Count, agentId);

        return Ok(new { removed = expired.Count });
    }

    /// <summary>
    /// GET /api/memories/browse?agentId=X&amp;category=Y&amp;search=Z&amp;includeExpired=false
    /// Browse memories with optional category filter and text search.
    /// Uses FTS5 when search is provided, with LIKE fallback.
    /// </summary>
    [HttpGet("browse")]
    public async Task<IActionResult> Browse(
        [FromQuery] string? agentId,
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] bool includeExpired = false,
        CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        if (string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { code = "missing_agent_id", message = "agentId query parameter is required." });

        var now = DateTime.UtcNow;

        // Use FTS5 search path when a search term is provided
        if (!string.IsNullOrWhiteSpace(search))
        {
            var results = await RecallHandler.SearchWithFts5Async(_db, agentId, search, category?.ToLowerInvariant(), null);
            // SearchWithFts5Async includes shared memories from other agents (by design for RECALL).
            // Browse is agent-scoped, so filter to only the requested agent.
            results = results.Where(m => m.AgentId == agentId).ToList();
            if (!includeExpired)
                results = results.Where(m => m.ExpiresAt == null || m.ExpiresAt > now).ToList();

            return Ok(new BrowseResponse
            {
                Total = results.Count,
                Memories = results.Select(MapToDto).ToList(),
            });
        }

        // Non-search: simple EF query
        IQueryable<AgentMemoryEntity> query = _db.AgentMemories.Where(m => m.AgentId == agentId);

        if (!includeExpired)
            query = query.Where(m => m.ExpiresAt == null || m.ExpiresAt > now);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(m => m.Category == category.ToLowerInvariant());

        query = query.OrderBy(m => m.Category).ThenBy(m => m.Key);

        var memories = await query.AsNoTracking().ToListAsync(ct);

        return Ok(new BrowseResponse
        {
            Total = memories.Count,
            Memories = memories.Select(MapToDto).ToList(),
        });
    }

    /// <summary>
    /// GET /api/memories/stats?agentId=X — per-category memory counts.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats([FromQuery] string? agentId, CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        if (string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { code = "missing_agent_id", message = "agentId query parameter is required." });

        var now = DateTime.UtcNow;
        var stats = await _db.AgentMemories
            .Where(m => m.AgentId == agentId)
            .GroupBy(m => m.Category)
            .Select(g => new CategoryStat
            {
                Category = g.Key,
                Total = g.Count(),
                Active = g.Count(m => m.ExpiresAt == null || m.ExpiresAt > now),
                Expired = g.Count(m => m.ExpiresAt != null && m.ExpiresAt <= now),
                LastUpdated = g.Max(m => m.UpdatedAt ?? m.CreatedAt),
            })
            .OrderByDescending(s => s.Active)
            .ToListAsync(ct);

        return Ok(new StatsResponse
        {
            AgentId = agentId,
            TotalMemories = stats.Sum(s => s.Total),
            ActiveMemories = stats.Sum(s => s.Active),
            ExpiredMemories = stats.Sum(s => s.Expired),
            Categories = stats,
        });
    }

    /// <summary>
    /// DELETE /api/memories?agentId=X&amp;key=Y — delete a single memory entry.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> Delete([FromQuery] string? agentId, [FromQuery] string? key, CancellationToken ct = default)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { code = "not_authenticated", message = "Authentication is required." });

        if (string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { code = "missing_agent_id", message = "agentId query parameter is required." });

        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { code = "missing_key", message = "key query parameter is required." });

        var entity = await _db.AgentMemories.FindAsync(new object[] { agentId, key }, ct);
        if (entity is null)
            return NotFound(new { code = "not_found", message = $"Memory '{key}' not found for agent '{agentId}'." });

        _db.AgentMemories.Remove(entity);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Memory deleted: agent={AgentId} key={Key}", agentId, key);

        return Ok(new { status = "deleted", agentId, key });
    }

    private static MemoryExportDto MapToDto(AgentMemoryEntity m) => new()
    {
        AgentId = m.AgentId,
        Category = m.Category,
        Key = m.Key,
        Value = m.Value,
        CreatedAt = m.CreatedAt,
        UpdatedAt = m.UpdatedAt,
        LastAccessedAt = m.LastAccessedAt,
        ExpiresAt = m.ExpiresAt,
    };

    public record MemoryExportDto
    {
        public string AgentId { get; init; } = "";
        public string Category { get; init; } = "";
        public string Key { get; init; } = "";
        public string Value { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public DateTime? LastAccessedAt { get; init; }
        public DateTime? ExpiresAt { get; init; }
    }

    public record MemoryImportRequest
    {
        [MaxLength(500)]
        public List<MemoryImportEntry> Memories { get; init; } = [];
    }

    public record MemoryImportEntry
    {
        [Required, StringLength(100)]
        public string AgentId { get; init; } = "";
        [Required, StringLength(200)]
        public string Category { get; init; } = "";
        [Required, StringLength(200)]
        public string Key { get; init; } = "";
        [Required, StringLength(500)]
        public string Value { get; init; } = "";
        [Range(1, 87_600)]
        public int? TtlHours { get; init; }
    }

    public record BrowseResponse
    {
        public int Total { get; init; }
        public List<MemoryExportDto> Memories { get; init; } = [];
    }

    public record StatsResponse
    {
        public string AgentId { get; init; } = "";
        public int TotalMemories { get; init; }
        public int ActiveMemories { get; init; }
        public int ExpiredMemories { get; init; }
        public List<CategoryStat> Categories { get; init; } = [];
    }

    public record CategoryStat
    {
        public string Category { get; init; } = "";
        public int Total { get; init; }
        public int Active { get; init; }
        public int Expired { get; init; }
        public DateTime LastUpdated { get; init; }
    }
}
