using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Read-only queries for conversation sessions — room history, session stats,
/// and context retrieval for agents. Extracted from ConversationSessionService
/// to separate query operations from lifecycle mutations.
/// </summary>
public sealed class ConversationSessionQueryService
{
    private readonly AgentAcademyDbContext _db;

    public ConversationSessionQueryService(AgentAcademyDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the summary from the most recently archived session for a room,
    /// or null if no prior session exists.
    /// </summary>
    public async Task<string?> GetSessionContextAsync(string roomId)
    {
        return await _db.ConversationSessions
            .Where(s => s.RoomId == roomId && s.Status == "Archived" && s.Summary != null)
            .OrderByDescending(s => s.SequenceNumber)
            .Select(s => s.Summary)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Returns the summary from the most recently archived session for a
    /// given sprint and stage. Used to inject previous-stage context into
    /// the next stage's conversation.
    /// </summary>
    public async Task<string?> GetStageContextAsync(string sprintId, string stage)
    {
        return await _db.ConversationSessions
            .Where(s => s.SprintId == sprintId
                && s.SprintStage == stage
                && s.Status == "Archived"
                && s.Summary != null)
            .OrderByDescending(s => s.SequenceNumber)
            .Select(s => s.Summary)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Returns one summary per stage for a sprint, deduplicated to the latest
    /// archived session per stage, ordered by canonical sprint stage sequence.
    /// Used to build a complete sprint context for agents.
    /// </summary>
    public async Task<List<(string Stage, string Summary)>> GetSprintContextAsync(string sprintId)
    {
        var sessions = await _db.ConversationSessions
            .Where(s => s.SprintId == sprintId
                && s.Status == "Archived"
                && s.Summary != null
                && s.SprintStage != null)
            .OrderByDescending(s => s.SequenceNumber)
            .Select(s => new { s.SprintStage, s.Summary })
            .ToListAsync();

        // Deduplicate: keep only the latest (highest sequence) per stage
        var latestPerStage = sessions
            .GroupBy(s => s.SprintStage!)
            .ToDictionary(g => g.Key, g => g.First().Summary!);

        // Order by canonical stage sequence
        return SprintStageService.Stages
            .Where(stage => latestPerStage.ContainsKey(stage))
            .Select(stage => (Stage: stage, Summary: latestPerStage[stage]))
            .ToList();
    }

    /// <summary>
    /// Lists conversation sessions for a specific room, ordered by sequence number descending.
    /// </summary>
    public async Task<(List<ConversationSessionSnapshot> Sessions, int TotalCount)> GetRoomSessionsAsync(
        string roomId, string? status = null, int limit = 20, int offset = 0)
    {
        var query = _db.ConversationSessions
            .Where(s => s.RoomId == roomId);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(s => s.Status == status);

        var totalCount = await query.CountAsync();
        var safeOffset = Math.Max(offset, 0);

        var sessions = await query
            .OrderByDescending(s => s.SequenceNumber)
            .Skip(safeOffset)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(s => new ConversationSessionSnapshot(
                s.Id, s.RoomId, s.RoomType, s.SequenceNumber,
                s.Status, s.Summary, s.MessageCount, s.CreatedAt, s.ArchivedAt,
                s.WorkspacePath))
            .ToListAsync();

        return (sessions, totalCount);
    }

    /// <summary>
    /// Lists conversation sessions across all rooms, ordered by creation date descending.
    /// Optionally filtered by workspace path for project-scoped queries.
    /// </summary>
    public async Task<(List<ConversationSessionSnapshot> Sessions, int TotalCount)> GetAllSessionsAsync(
        string? status = null, int limit = 20, int offset = 0, int? hoursBack = null,
        string? workspacePath = null)
    {
        var query = _db.ConversationSessions.AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(s => s.Status == status);

        if (hoursBack.HasValue)
        {
            var since = DateTime.UtcNow.AddHours(-hoursBack.Value);
            query = query.Where(s => s.CreatedAt >= since);
        }

        if (!string.IsNullOrEmpty(workspacePath))
            query = query.Where(s => s.WorkspacePath == workspacePath);

        var totalCount = await query.CountAsync();
        var safeOffset = Math.Max(offset, 0);

        var sessions = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip(safeOffset)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(s => new ConversationSessionSnapshot(
                s.Id, s.RoomId, s.RoomType, s.SequenceNumber,
                s.Status, s.Summary, s.MessageCount, s.CreatedAt, s.ArchivedAt,
                s.WorkspacePath))
            .ToListAsync();

        return (sessions, totalCount);
    }

    /// <summary>
    /// Returns a summary of session stats: total sessions, active/archived counts,
    /// total messages across all sessions. Optionally scoped to a workspace.
    /// </summary>
    public async Task<SessionStats> GetSessionStatsAsync(int? hoursBack = null,
        string? workspacePath = null)
    {
        var query = _db.ConversationSessions.AsQueryable();

        if (hoursBack.HasValue)
        {
            var since = DateTime.UtcNow.AddHours(-hoursBack.Value);
            query = query.Where(s => s.CreatedAt >= since);
        }

        if (!string.IsNullOrEmpty(workspacePath))
            query = query.Where(s => s.WorkspacePath == workspacePath);

        var stats = await query
            .GroupBy(_ => 1)
            .Select(g => new SessionStats(
                g.Count(),
                g.Count(s => s.Status == "Active"),
                g.Count(s => s.Status == "Archived"),
                g.Sum(s => s.MessageCount)))
            .FirstOrDefaultAsync();

        return stats ?? new SessionStats(0, 0, 0, 0);
    }
}
