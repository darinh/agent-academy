using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Records and queries agent error events. Singleton service that
/// creates its own DB scopes (same pattern as LlmUsageTracker).
/// Recording failures never propagate to agent execution.
/// </summary>
public sealed class AgentErrorTracker : IAgentErrorTracker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentErrorTracker> _logger;

    public AgentErrorTracker(
        IServiceScopeFactory scopeFactory,
        ILogger<AgentErrorTracker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Records an agent error. Failures are logged but never propagated.
    /// </summary>
    public async Task RecordAsync(
        string agentId,
        string? roomId,
        string errorType,
        string message,
        bool recoverable,
        bool retried = false,
        int? retryAttempt = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            var entity = new AgentErrorEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                AgentId = agentId,
                RoomId = roomId,
                ErrorType = errorType,
                Message = TruncateMessage(message),
                Recoverable = recoverable,
                Retried = retried,
                RetryAttempt = retryAttempt,
                OccurredAt = DateTime.UtcNow,
            };

            db.AgentErrors.Add(entity);
            await db.SaveChangesAsync();

            _logger.LogDebug(
                "Recorded agent error: agent={AgentId} type={ErrorType} recoverable={Recoverable}",
                agentId, errorType, recoverable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record agent error for {AgentId} — error data lost but agent unaffected",
                agentId);
        }
    }

    /// <summary>
    /// Returns errors for a specific room, most recent first.
    /// </summary>
    public async Task<List<ErrorRecord>> GetRoomErrorsAsync(string roomId, int limit = 50)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        return await db.AgentErrors
            .Where(e => e.RoomId == roomId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .Select(e => new ErrorRecord(
                e.AgentId,
                e.RoomId ?? "",
                e.ErrorType,
                e.Message,
                e.Recoverable,
                e.OccurredAt
            ))
            .ToListAsync();
    }

    /// <summary>
    /// Returns recent errors across all rooms, optionally filtered by agent or time.
    /// </summary>
    public async Task<List<ErrorRecord>> GetRecentErrorsAsync(
        string? agentId = null,
        DateTime? since = null,
        int limit = 50)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var query = db.AgentErrors.AsQueryable();

        if (agentId is not null)
            query = query.Where(e => e.AgentId == agentId);
        if (since.HasValue)
            query = query.Where(e => e.OccurredAt >= since.Value);

        return await query
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .Select(e => new ErrorRecord(
                e.AgentId,
                e.RoomId ?? "",
                e.ErrorType,
                e.Message,
                e.Recoverable,
                e.OccurredAt
            ))
            .ToListAsync();
    }

    /// <summary>
    /// Returns error counts grouped by type for a time window.
    /// </summary>
    public async Task<ErrorSummary> GetErrorSummaryAsync(DateTime? since = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        var query = db.AgentErrors.AsQueryable();
        if (since.HasValue)
            query = query.Where(e => e.OccurredAt >= since.Value);

        var stats = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Recoverable = g.Count(e => e.Recoverable),
                Unrecoverable = g.Count(e => !e.Recoverable),
            })
            .FirstOrDefaultAsync();

        var byType = await query
            .GroupBy(e => e.ErrorType)
            .Select(g => new ErrorCountByType(g.Key, g.Count()))
            .ToListAsync();

        var byAgent = await query
            .GroupBy(e => e.AgentId)
            .Select(g => new ErrorCountByAgent(g.Key, g.Count()))
            .ToListAsync();

        return new ErrorSummary(
            TotalErrors: stats?.Total ?? 0,
            RecoverableErrors: stats?.Recoverable ?? 0,
            UnrecoverableErrors: stats?.Unrecoverable ?? 0,
            ByType: byType,
            ByAgent: byAgent
        );
    }

    private static string TruncateMessage(string message)
    {
        const int maxLength = 2000;
        // Redact potential secrets: bearer tokens, API keys, connection strings
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"(Bearer\s+|token[=:]\s*|key[=:]\s*|password[=:]\s*|secret[=:]\s*)[^\s""',;}{)]+",
            "$1[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return sanitized.Length > maxLength
            ? sanitized[..maxLength] + "…"
            : sanitized;
    }
}
