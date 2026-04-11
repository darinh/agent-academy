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
}
