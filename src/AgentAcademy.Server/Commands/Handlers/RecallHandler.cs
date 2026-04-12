using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RECALL — searches the agent's memories using FTS5 full-text search
/// with BM25 ranking when a free-text query is provided, falling back to LIKE
/// if FTS5 is unavailable. Category and key filters use exact/LIKE matching.
/// Filters out expired memories and updates LastAccessedAt on returned results.
/// </summary>
public sealed class RecallHandler : ICommandHandler
{
    public string CommandName => "RECALL";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();

        var hasQuery = command.Args.TryGetValue("query", out var qObj) && qObj is string searchQuery && !string.IsNullOrWhiteSpace(searchQuery);
        var hasCategory = command.Args.TryGetValue("category", out var catObj) && catObj is string category && !string.IsNullOrWhiteSpace(category);
        var hasKey = command.Args.TryGetValue("key", out var keyObj) && keyObj is string key && !string.IsNullOrWhiteSpace(key);

        // Check for include_expired flag (defaults to false)
        var includeExpired = command.Args.TryGetValue("include_expired", out var expObj) &&
            (expObj is true || (expObj is string expStr && expStr.Equals("true", StringComparison.OrdinalIgnoreCase)));

        List<AgentMemoryEntity> memories;
        var now = DateTime.UtcNow;

        if (hasQuery)
        {
            var queryText = (string)qObj!;
            var categoryFilter = hasCategory ? (string)catObj! : null;
            var keyFilter = hasKey ? (string)keyObj! : null;

            memories = await SearchWithFts5Async(db, context.AgentId, queryText, categoryFilter, keyFilter);
        }
        else
        {
            // Include own memories + shared memories from all agents
            var query = db.AgentMemories.Where(m => m.AgentId == context.AgentId || m.Category == "shared");

            if (hasCategory)
                query = query.Where(m => m.Category == (string)catObj!);

            if (hasKey)
                query = query.Where(m => m.Key == (string)keyObj!);

            memories = await query.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();
        }

        // Filter out expired memories unless explicitly requested
        if (!includeExpired)
            memories = memories.Where(m => m.ExpiresAt == null || m.ExpiresAt > now).ToList();

        // Update LastAccessedAt for returned memories
        if (memories.Count > 0)
        {
            await UpdateLastAccessedAsync(db, memories, now);
        }

        var result = memories.Select(m => new Dictionary<string, object?>
        {
            ["category"] = m.Category,
            ["key"] = m.Key,
            ["value"] = m.Value,
            ["createdAt"] = m.CreatedAt.ToString("o"),
            ["updatedAt"] = m.UpdatedAt?.ToString("o"),
            ["agentId"] = m.Category == "shared" && m.AgentId != context.AgentId ? m.AgentId : null,
            ["expiresAt"] = m.ExpiresAt?.ToString("o"),
            ["stale"] = IsStale(m, now) ? true : null
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?> { ["memories"] = result, ["count"] = result.Count }
        };
    }

    /// <summary>
    /// A memory is considered stale if it hasn't been accessed in 30+ days
    /// and has no explicit TTL (TTL memories have their own lifecycle).
    /// </summary>
    internal static bool IsStale(AgentMemoryEntity m, DateTime now)
    {
        if (m.ExpiresAt.HasValue) return false; // TTL memories aren't marked stale
        var lastActivity = m.LastAccessedAt ?? m.UpdatedAt ?? m.CreatedAt;
        return (now - lastActivity).TotalDays >= 30;
    }

    /// <summary>
    /// Searches agent memories using FTS5 MATCH with BM25 ranking.
    /// Falls back to LIKE search if FTS5 table doesn't exist.
    /// </summary>
    internal static async Task<List<AgentMemoryEntity>> SearchWithFts5Async(
        AgentAcademyDbContext db,
        string agentId,
        string queryText,
        string? categoryFilter,
        string? keyFilter)
    {
        var ftsQuery = BuildFts5Query(queryText);

        try
        {
            // FTS5 search with BM25 ranking, joined back to main table for full entity.
            // FTS5 table only indexes key+value (not agent_id) to avoid false positives.
            // Include own memories + shared memories from all agents.
            var sql = """
                SELECT m.AgentId, m.Key, m.Category, m.Value, m.CreatedAt, m.UpdatedAt, m.LastAccessedAt, m.ExpiresAt
                FROM agent_memories_fts fts
                INNER JOIN agent_memories m ON m.rowid = fts.rowid
                WHERE agent_memories_fts MATCH {0}
                  AND (m.AgentId = {1} OR m.Category = 'shared')
                """;

            if (categoryFilter is not null)
                sql += "\n  AND m.Category = {2}";
            if (keyFilter is not null)
                sql += categoryFilter is not null ? "\n  AND m.Key = {3}" : "\n  AND m.Key = {2}";

            sql += "\nORDER BY bm25(agent_memories_fts) ASC";

            // Build parameters array based on which filters are active
            var parameters = new List<object> { ftsQuery, agentId };
            if (categoryFilter is not null) parameters.Add(categoryFilter);
            if (keyFilter is not null) parameters.Add(keyFilter);

            return await db.AgentMemories
                .FromSqlRaw(sql, parameters.ToArray())
                .AsNoTracking()
                .ToListAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table: agent_memories_fts"))
        {
            // FTS5 table not available (migration not applied) — fall back to LIKE search
            return await FallbackLikeSearchAsync(db, agentId, queryText, categoryFilter, keyFilter);
        }
    }

    /// <summary>
    /// Builds an FTS5-safe query string. Escapes special characters and
    /// converts space-separated words to implicit AND terms.
    /// </summary>
    internal static string BuildFts5Query(string input)
    {
        // FTS5 special chars: " * ^ : ( ) { } [ ] + - ~ !
        // Strategy: quote each term individually so special chars in user input are treated as literals
        var terms = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return "\"\"";

        // Each term becomes a quoted phrase — "term1" "term2" (implicit AND in FTS5)
        // Double any internal quotes
        var quoted = terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"");
        return string.Join(" ", quoted);
    }

    private static async Task<List<AgentMemoryEntity>> FallbackLikeSearchAsync(
        AgentAcademyDbContext db,
        string agentId,
        string queryText,
        string? categoryFilter,
        string? keyFilter)
    {
        var pattern = $"%{queryText}%";
        var query = db.AgentMemories
            .Where(m => m.AgentId == agentId || m.Category == "shared")
            .Where(m => EF.Functions.Like(m.Key, pattern) || EF.Functions.Like(m.Value, pattern));

        if (categoryFilter is not null)
            query = query.Where(m => m.Category == categoryFilter);
        if (keyFilter is not null)
            query = query.Where(m => m.Key == keyFilter);

        return await query.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();
    }

    /// <summary>
    /// Batch-update LastAccessedAt for recalled memories using a single SQL statement.
    /// </summary>
    private static async Task UpdateLastAccessedAsync(
        AgentAcademyDbContext db,
        List<AgentMemoryEntity> memories,
        DateTime now)
    {
        try
        {
            // Build a single UPDATE with IN clause for efficiency
            var keys = memories.Select(m => (m.AgentId, m.Key)).Distinct().ToList();
            foreach (var group in keys.GroupBy(k => k.AgentId))
            {
                var agentId = group.Key;
                var keyList = group.Select(g => g.Key).ToList();
                // Use parameterized batch — one UPDATE per agent
                var placeholders = string.Join(", ", keyList.Select((_, i) => $"{{{i + 2}}}"));
                var sql = $"UPDATE agent_memories SET LastAccessedAt = {{0}} WHERE AgentId = {{1}} AND Key IN ({placeholders})";
                var parameters = new List<object> { now, agentId };
                parameters.AddRange(keyList);
                await db.Database.ExecuteSqlRawAsync(sql, parameters.ToArray());
            }
        }
        catch
        {
            // LastAccessedAt update is best-effort — don't fail the recall
        }
    }
}
