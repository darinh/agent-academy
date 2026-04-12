using System.Runtime.CompilerServices;
using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Full-text search across workspace messages (room + breakout) and tasks.
/// Uses FTS5 virtual tables with BM25 ranking, falling back to LIKE on older databases.
/// </summary>
public sealed class SearchService
{
    private readonly AgentAcademyDbContext _db;
    private readonly ILogger<SearchService> _logger;

    private const int MaxLimit = 100;
    private const int DefaultLimit = 25;
    private const int SnippetLength = 80;

    public SearchService(AgentAcademyDbContext db, ILogger<SearchService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Search messages and/or tasks matching the given query.
    /// </summary>
    /// <param name="query">User search query (free text).</param>
    /// <param name="scope">"messages", "tasks", or "all" (default).</param>
    /// <param name="messageLimit">Max message results.</param>
    /// <param name="taskLimit">Max task results.</param>
    /// <param name="workspacePath">Active workspace to scope results to.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SearchResults> SearchAsync(
        string query,
        string scope = "all",
        int messageLimit = DefaultLimit,
        int taskLimit = DefaultLimit,
        string? workspacePath = null,
        CancellationToken ct = default)
    {
        query = query.Trim();
        messageLimit = Math.Clamp(messageLimit, 1, MaxLimit);
        taskLimit = Math.Clamp(taskLimit, 1, MaxLimit);

        var messages = new List<MessageSearchResult>();
        var tasks = new List<TaskSearchResult>();

        var includeMessages = scope is "all" or "messages";
        var includeTasks = scope is "all" or "tasks";

        if (includeMessages)
        {
            var roomMessages = await SearchRoomMessagesAsync(query, messageLimit, workspacePath, ct);
            var breakoutMessages = await SearchBreakoutMessagesAsync(query, messageLimit, workspacePath, ct);

            // Merge and take top N by relevance (FTS5 rank, or recency for LIKE fallback)
            messages = roomMessages
                .Concat(breakoutMessages)
                .OrderByDescending(m => m.SentAt)
                .Take(messageLimit)
                .ToList();
        }

        if (includeTasks)
        {
            tasks = await SearchTasksAsync(query, taskLimit, workspacePath, ct);
        }

        return new SearchResults(messages, tasks, messages.Count + tasks.Count, query);
    }

    private async Task<List<MessageSearchResult>> SearchRoomMessagesAsync(
        string query, int limit, string? workspacePath, CancellationToken ct)
    {
        var ftsQuery = BuildFts5Query(query);

        try
        {
            // FTS5 search on room messages joined to rooms for name + workspace filtering
            var sql = workspacePath is not null
                ? FormattableStringFactory.Create(
                    """
                    SELECT m.Id, m.RoomId, r.Name AS RoomName, m.SenderName, m.SenderKind, m.SenderRole,
                           snippet(messages_fts, 1, '«', '»', '…', {1}) AS Snippet,
                           m.SentAt, m.SessionId
                    FROM messages_fts
                    JOIN messages m ON m.rowid = messages_fts.rowid
                    JOIN rooms r ON r.Id = m.RoomId
                    WHERE messages_fts MATCH {0}
                      AND r.WorkspacePath = {2}
                      AND m.SenderKind != 'System'
                    ORDER BY rank
                    LIMIT {3}
                    """,
                    ftsQuery, SnippetLength, workspacePath, limit)
                : FormattableStringFactory.Create(
                    """
                    SELECT m.Id, m.RoomId, r.Name AS RoomName, m.SenderName, m.SenderKind, m.SenderRole,
                           snippet(messages_fts, 1, '«', '»', '…', {1}) AS Snippet,
                           m.SentAt, m.SessionId
                    FROM messages_fts
                    JOIN messages m ON m.rowid = messages_fts.rowid
                    JOIN rooms r ON r.Id = m.RoomId
                    WHERE messages_fts MATCH {0}
                      AND m.SenderKind != 'System'
                    ORDER BY rank
                    LIMIT {2}
                    """,
                    ftsQuery, SnippetLength, limit);

            var results = await _db.Database.SqlQuery<MessageSearchRow>(sql).ToListAsync(ct);

            return results.Select(r => new MessageSearchResult(
                r.Id, r.RoomId, r.RoomName, r.SenderName, r.SenderKind, r.SenderRole,
                r.Snippet, r.SentAt, r.SessionId, "room")).ToList();
        }
        catch (Exception ex) when (ex.Message.Contains("no such table") || ex.Message.Contains("fts5"))
        {
            _logger.LogWarning(ex, "FTS5 table messages_fts not available, falling back to LIKE search");
            return await FallbackMessageSearchAsync(query, limit, workspacePath, ct);
        }
    }

    private async Task<List<MessageSearchResult>> SearchBreakoutMessagesAsync(
        string query, int limit, string? workspacePath, CancellationToken ct)
    {
        var ftsQuery = BuildFts5Query(query);

        try
        {
            var sql = workspacePath is not null
                ? FormattableStringFactory.Create(
                    """
                    SELECT bm.Id, br.ParentRoomId AS RoomId, r.Name AS RoomName, bm.SenderName,
                           bm.SenderKind, bm.SenderRole,
                           snippet(breakout_messages_fts, 1, '«', '»', '…', {1}) AS Snippet,
                           bm.SentAt, bm.SessionId, br.Id AS BreakoutRoomId
                    FROM breakout_messages_fts
                    JOIN breakout_messages bm ON bm.rowid = breakout_messages_fts.rowid
                    JOIN breakout_rooms br ON br.Id = bm.BreakoutRoomId
                    JOIN rooms r ON r.Id = br.ParentRoomId
                    WHERE breakout_messages_fts MATCH {0}
                      AND r.WorkspacePath = {2}
                      AND bm.SenderKind != 'System'
                    ORDER BY rank
                    LIMIT {3}
                    """,
                    ftsQuery, SnippetLength, workspacePath, limit)
                : FormattableStringFactory.Create(
                    """
                    SELECT bm.Id, br.ParentRoomId AS RoomId, r.Name AS RoomName, bm.SenderName,
                           bm.SenderKind, bm.SenderRole,
                           snippet(breakout_messages_fts, 1, '«', '»', '…', {1}) AS Snippet,
                           bm.SentAt, bm.SessionId, br.Id AS BreakoutRoomId
                    FROM breakout_messages_fts
                    JOIN breakout_messages bm ON bm.rowid = breakout_messages_fts.rowid
                    JOIN breakout_rooms br ON br.Id = bm.BreakoutRoomId
                    JOIN rooms r ON r.Id = br.ParentRoomId
                    WHERE breakout_messages_fts MATCH {0}
                      AND bm.SenderKind != 'System'
                    ORDER BY rank
                    LIMIT {2}
                    """,
                    ftsQuery, SnippetLength, limit);

            var results = await _db.Database.SqlQuery<BreakoutMessageSearchRow>(sql).ToListAsync(ct);

            return results.Select(r => new MessageSearchResult(
                r.Id, r.RoomId, r.RoomName, r.SenderName, r.SenderKind, r.SenderRole,
                r.Snippet, r.SentAt, r.SessionId, "breakout")).ToList();
        }
        catch (Exception ex) when (ex.Message.Contains("no such table") || ex.Message.Contains("fts5"))
        {
            _logger.LogWarning(ex, "FTS5 table breakout_messages_fts not available, falling back to LIKE search");
            return await FallbackBreakoutSearchAsync(query, limit, workspacePath, ct);
        }
    }

    private async Task<List<TaskSearchResult>> SearchTasksAsync(
        string query, int limit, string? workspacePath, CancellationToken ct)
    {
        var ftsQuery = BuildFts5Query(query);

        try
        {
            var sql = workspacePath is not null
                ? FormattableStringFactory.Create(
                    """
                    SELECT t.Id, t.Title, t.Status, t.AssignedAgentName,
                           snippet(tasks_fts, 1, '«', '»', '…', {1}) AS Snippet,
                           t.CreatedAt, t.RoomId
                    FROM tasks_fts
                    JOIN tasks t ON t.rowid = tasks_fts.rowid
                    WHERE tasks_fts MATCH {0}
                      AND t.WorkspacePath = {2}
                    ORDER BY rank
                    LIMIT {3}
                    """,
                    ftsQuery, SnippetLength, workspacePath, limit)
                : FormattableStringFactory.Create(
                    """
                    SELECT t.Id, t.Title, t.Status, t.AssignedAgentName,
                           snippet(tasks_fts, 1, '«', '»', '…', {1}) AS Snippet,
                           t.CreatedAt, t.RoomId
                    FROM tasks_fts
                    JOIN tasks t ON t.rowid = tasks_fts.rowid
                    WHERE tasks_fts MATCH {0}
                    ORDER BY rank
                    LIMIT {2}
                    """,
                    ftsQuery, SnippetLength, limit);

            var results = await _db.Database.SqlQuery<TaskSearchRow>(sql).ToListAsync(ct);

            return results.Select(r => new TaskSearchResult(
                r.Id, r.Title, r.Status, r.AssignedAgentName, r.Snippet, r.CreatedAt, r.RoomId)).ToList();
        }
        catch (Exception ex) when (ex.Message.Contains("no such table") || ex.Message.Contains("fts5"))
        {
            _logger.LogWarning(ex, "FTS5 table tasks_fts not available, falling back to LIKE search");
            return await FallbackTaskSearchAsync(query, limit, workspacePath, ct);
        }
    }

    // ── LIKE Fallbacks ──────────────────────────────────────────────────

    private async Task<List<MessageSearchResult>> FallbackMessageSearchAsync(
        string query, int limit, string? workspacePath, CancellationToken ct)
    {
        var pattern = $"%{query}%";
        var q = _db.Messages
            .Include(m => m.Room)
            .Where(m => m.SenderKind != "System")
            .Where(m => EF.Functions.Like(m.Content, pattern) || EF.Functions.Like(m.SenderName, pattern));

        if (workspacePath is not null)
            q = q.Where(m => m.Room.WorkspacePath == workspacePath);

        var results = await q
            .OrderByDescending(m => m.SentAt)
            .Take(limit)
            .Select(m => new MessageSearchResult(
                m.Id, m.RoomId, m.Room.Name, m.SenderName, m.SenderKind, m.SenderRole,
                m.Content.Length > SnippetLength * 2 ? m.Content.Substring(0, SnippetLength * 2) + "…" : m.Content,
                m.SentAt, m.SessionId, "room"))
            .ToListAsync(ct);

        return results;
    }

    private async Task<List<MessageSearchResult>> FallbackBreakoutSearchAsync(
        string query, int limit, string? workspacePath, CancellationToken ct)
    {
        var pattern = $"%{query}%";
        var q = _db.BreakoutMessages
            .Include(m => m.BreakoutRoom)
            .ThenInclude(br => br.ParentRoom)
            .Where(m => m.SenderKind != "System")
            .Where(m => EF.Functions.Like(m.Content, pattern) || EF.Functions.Like(m.SenderName, pattern));

        if (workspacePath is not null)
            q = q.Where(m => m.BreakoutRoom.ParentRoom.WorkspacePath == workspacePath);

        var results = await q
            .OrderByDescending(m => m.SentAt)
            .Take(limit)
            .Select(m => new MessageSearchResult(
                m.Id, m.BreakoutRoom.ParentRoomId, m.BreakoutRoom.ParentRoom.Name, m.SenderName, m.SenderKind, m.SenderRole,
                m.Content.Length > SnippetLength * 2 ? m.Content.Substring(0, SnippetLength * 2) + "…" : m.Content,
                m.SentAt, m.SessionId, "breakout"))
            .ToListAsync(ct);

        return results;
    }

    private async Task<List<TaskSearchResult>> FallbackTaskSearchAsync(
        string query, int limit, string? workspacePath, CancellationToken ct)
    {
        var pattern = $"%{query}%";
        var q = _db.Tasks
            .Where(t => EF.Functions.Like(t.Title, pattern)
                     || EF.Functions.Like(t.Description, pattern)
                     || EF.Functions.Like(t.SuccessCriteria, pattern));

        if (workspacePath is not null)
            q = q.Where(t => t.WorkspacePath == workspacePath);

        var results = await q
            .OrderByDescending(t => t.CreatedAt)
            .Take(limit)
            .Select(t => new TaskSearchResult(
                t.Id, t.Title, t.Status, t.AssignedAgentName,
                t.Description.Length > SnippetLength * 2 ? t.Description.Substring(0, SnippetLength * 2) + "…" : t.Description,
                t.CreatedAt, t.RoomId))
            .ToListAsync(ct);

        return results;
    }

    // ── FTS5 Query Builder ──────────────────────────────────────────────

    /// <summary>
    /// Builds an FTS5-safe query string. Escapes special characters and
    /// converts space-separated words to implicit AND terms.
    /// Reuses the same pattern as RecallHandler.
    /// </summary>
    internal static string BuildFts5Query(string input)
    {
        var terms = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return "\"\"";

        var quoted = terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"");
        return string.Join(" ", quoted);
    }

    // ── Row Types for SqlQuery<T> ───────────────────────────────────────

    internal sealed class MessageSearchRow
    {
        public string Id { get; set; } = "";
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string SenderKind { get; set; } = "";
        public string? SenderRole { get; set; }
        public string Snippet { get; set; } = "";
        public DateTime SentAt { get; set; }
        public string? SessionId { get; set; }
    }

    internal sealed class BreakoutMessageSearchRow
    {
        public string Id { get; set; } = "";
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string SenderKind { get; set; } = "";
        public string? SenderRole { get; set; }
        public string Snippet { get; set; } = "";
        public DateTime SentAt { get; set; }
        public string? SessionId { get; set; }
        public string BreakoutRoomId { get; set; } = "";
    }

    internal sealed class TaskSearchRow
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public string? AssignedAgentName { get; set; }
        public string Snippet { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? RoomId { get; set; }
    }
}
