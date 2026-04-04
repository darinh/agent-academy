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
/// </summary>
public sealed class RecallHandler : ICommandHandler
{
    public string CommandName => "RECALL";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();

        var hasQuery = command.Args.TryGetValue("query", out var qObj) && qObj is string searchQuery && !string.IsNullOrWhiteSpace(searchQuery);
        var hasCategory = command.Args.TryGetValue("category", out var catObj) && catObj is string category && !string.IsNullOrWhiteSpace(category);
        var hasKey = command.Args.TryGetValue("key", out var keyObj) && keyObj is string key && !string.IsNullOrWhiteSpace(key);

        List<AgentMemoryEntity> memories;

        if (hasQuery)
        {
            var queryText = (string)qObj!;
            var categoryFilter = hasCategory ? (string)catObj! : null;
            var keyFilter = hasKey ? (string)keyObj! : null;

            memories = await SearchWithFts5Async(db, context.AgentId, queryText, categoryFilter, keyFilter);
        }
        else
        {
            var query = db.AgentMemories.Where(m => m.AgentId == context.AgentId);

            if (hasCategory)
                query = query.Where(m => m.Category == (string)catObj!);

            if (hasKey)
                query = query.Where(m => m.Key == (string)keyObj!);

            memories = await query.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();
        }

        var result = memories.Select(m => new Dictionary<string, object?>
        {
            ["category"] = m.Category,
            ["key"] = m.Key,
            ["value"] = m.Value,
            ["createdAt"] = m.CreatedAt.ToString("o"),
            ["updatedAt"] = m.UpdatedAt?.ToString("o")
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?> { ["memories"] = result, ["count"] = result.Count }
        };
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
            var sql = """
                SELECT m.AgentId, m.Key, m.Category, m.Value, m.CreatedAt, m.UpdatedAt
                FROM agent_memories_fts fts
                INNER JOIN agent_memories m ON m.rowid = fts.rowid
                WHERE agent_memories_fts MATCH {0}
                  AND m.AgentId = {1}
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
            .Where(m => m.AgentId == agentId)
            .Where(m => EF.Functions.Like(m.Key, pattern) || EF.Functions.Like(m.Value, pattern));

        if (categoryFilter is not null)
            query = query.Where(m => m.Category == categoryFilter);
        if (keyFilter is not null)
            query = query.Where(m => m.Key == keyFilter);

        return await query.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();
    }
}
