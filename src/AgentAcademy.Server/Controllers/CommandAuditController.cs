using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Read-only audit log and statistics endpoints for command execution history.
/// </summary>
[ApiController]
[Route("api/commands/audit")]
public sealed class CommandAuditController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AppAuthSetup _authSetup;

    public CommandAuditController(IServiceScopeFactory scopeFactory, AppAuthSetup authSetup)
    {
        _scopeFactory = scopeFactory;
        _authSetup = authSetup;
    }

    /// <summary>
    /// GET /api/commands/audit — paginated command audit log with optional filters.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] string? agentId = null,
        [FromQuery] string? command = null,
        [FromQuery] string? status = null,
        [FromQuery] int? hoursBack = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        if (_authSetup.AnyAuthEnabled && User.Identity?.IsAuthenticated != true)
            return Unauthorized(ApiProblem.Unauthorized("Authentication is required.", "not_authenticated"));

        if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
            return BadRequest(ApiProblem.BadRequest("hoursBack must be between 1 and 8760.", "invalid_hours_back"));

        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(offset, 0);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var query = db.CommandAudits.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(agentId))
            query = query.Where(a => a.AgentId == agentId);

        if (!string.IsNullOrWhiteSpace(command))
            query = query.Where(a => a.Command == command.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(status))
        {
            // Normalize common casing variants
            var normalized = status switch
            {
                var s when s.Equals("Success", StringComparison.OrdinalIgnoreCase) => "Success",
                var s when s.Equals("Error", StringComparison.OrdinalIgnoreCase) => "Error",
                var s when s.Equals("Denied", StringComparison.OrdinalIgnoreCase) => "Denied",
                var s when s.Equals("Pending", StringComparison.OrdinalIgnoreCase) => "Pending",
                _ => status
            };
            query = query.Where(a => a.Status == normalized);
        }

        if (hoursBack.HasValue)
        {
            var since = DateTime.UtcNow.AddHours(-hoursBack.Value);
            query = query.Where(a => a.Timestamp >= since);
        }

        var total = await query.CountAsync();

        var records = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip(offset)
            .Take(limit)
            .Select(a => new AuditLogEntry(
                a.Id,
                a.CorrelationId,
                a.AgentId,
                a.Source,
                a.Command,
                a.Status,
                a.ErrorMessage,
                a.ErrorCode,
                a.RoomId,
                a.Timestamp))
            .ToListAsync();

        return Ok(new AuditLogResponse(records, total, limit, offset));
    }

    /// <summary>
    /// GET /api/commands/audit/stats — aggregate command execution statistics.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetAuditStats([FromQuery] int? hoursBack = null)
    {
        if (_authSetup.AnyAuthEnabled && User.Identity?.IsAuthenticated != true)
            return Unauthorized(ApiProblem.Unauthorized("Authentication is required.", "not_authenticated"));

        if (hoursBack.HasValue && (hoursBack.Value < 1 || hoursBack.Value > 8760))
            return BadRequest(ApiProblem.BadRequest("hoursBack must be between 1 and 8760.", "invalid_hours_back"));

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var query = db.CommandAudits.AsNoTracking().AsQueryable();

        if (hoursBack.HasValue)
        {
            var since = DateTime.UtcNow.AddHours(-hoursBack.Value);
            query = query.Where(a => a.Timestamp >= since);
        }

        var totalCommands = await query.CountAsync();

        var byStatus = await query
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        var byAgent = await query
            .GroupBy(a => a.AgentId)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync();

        var byCommand = await query
            .GroupBy(a => a.Command)
            .Select(g => new { Command = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(20)
            .ToListAsync();

        return Ok(new AuditStatsResponse(
            TotalCommands: totalCommands,
            ByStatus: byStatus.ToDictionary(x => x.Status, x => x.Count),
            ByAgent: byAgent.ToDictionary(x => x.AgentId, x => x.Count),
            ByCommand: byCommand.ToDictionary(x => x.Command, x => x.Count),
            WindowHours: hoursBack));
    }
}
