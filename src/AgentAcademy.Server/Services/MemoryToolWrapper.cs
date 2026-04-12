using System.ComponentModel;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Wrapper that captures agent identity for memory tool functions.
/// </summary>
internal sealed class MemoryToolWrapper
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly string _agentId;

    internal MemoryToolWrapper(
        IServiceScopeFactory scopeFactory, ILogger logger, string agentId)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _agentId = agentId;
    }

    [Description("Store a memory that persists across sessions.")]
    internal async Task<string> RememberAsync(
        [Description("Unique key for this memory (e.g., 'auth-pattern', 'deploy-gotcha')")] string key,
        [Description("The knowledge to remember")] string value,
        [Description("Category: decision, lesson, pattern, preference, invariant, risk, gotcha, incident, constraint, finding, spec-drift, mapping, verification, gap-pattern, shared")] string category,
        [Description("Optional time-to-live in hours (max 87600). Memory expires after this. Omit for permanent.")] int? ttl = null,
        [Description("Set to true to remove any existing TTL and make the memory permanent")] bool permanent = false)
    {
        _logger.LogDebug("Tool call: remember by {AgentId} (key={Key}, category={Category})",
            _agentId, key, category);

        if (string.IsNullOrWhiteSpace(key))
            return "Error: key is required.";
        if (string.IsNullOrWhiteSpace(value))
            return "Error: value is required.";
        if (string.IsNullOrWhiteSpace(category))
            return "Error: category is required.";

        if (!RememberHandler.ValidCategories.Contains(category))
            return $"Error: Invalid category '{category}'. Valid: {string.Join(", ", RememberHandler.ValidCategories.Order())}";

        if (ttl.HasValue && (ttl.Value <= 0 || ttl.Value > 87600))
            return "Error: ttl must be between 1 and 87600 hours.";

        category = category.ToLowerInvariant();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            var existing = await db.AgentMemories.FindAsync(_agentId, key);
            var now = DateTime.UtcNow;
            DateTime? expiresAt = ttl.HasValue ? now.AddHours(ttl.Value) : null;

            if (existing != null)
            {
                existing.Category = category;
                existing.Value = value;
                existing.UpdatedAt = now;
                if (permanent)
                    existing.ExpiresAt = null;
                else if (ttl.HasValue)
                    existing.ExpiresAt = expiresAt;
            }
            else
            {
                db.AgentMemories.Add(new AgentMemoryEntity
                {
                    AgentId = _agentId,
                    Key = key,
                    Category = category,
                    Value = value,
                    CreatedAt = now,
                    ExpiresAt = expiresAt
                });
            }

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException) when (existing == null)
            {
                // Concurrent insert race — retry as update
                db.ChangeTracker.Clear();
                var conflict = await db.AgentMemories.FindAsync(_agentId, key);
                if (conflict != null)
                {
                    conflict.Category = category;
                    conflict.Value = value;
                    conflict.UpdatedAt = now;
                    if (permanent)
                        conflict.ExpiresAt = null;
                    else if (ttl.HasValue)
                        conflict.ExpiresAt = expiresAt;
                    await db.SaveChangesAsync();
                    existing = conflict; // for the action message
                }
            }

            var action = existing != null ? "updated" : "created";
            var result = $"Memory {action}: [{category}] {key}";
            if (permanent)
                result += " (permanent)";
            else if (expiresAt.HasValue)
                result += $" (expires: {expiresAt.Value:u})";
            return result;
        }
        catch (Exception ex)
        {
            return $"Error storing memory: {ex.Message}";
        }
    }

    [Description("Search and retrieve memories.")]
    internal async Task<string> RecallAsync(
        [Description("Free-text search query (uses full-text search with BM25 ranking)")] string? query = null,
        [Description("Filter by category")] string? category = null,
        [Description("Filter by exact key")] string? key = null,
        [Description("Include expired memories (default: false)")] bool includeExpired = false)
    {
        _logger.LogDebug("Tool call: recall by {AgentId} (query={Query}, category={Category})",
            _agentId, query, category);

        // Normalize category to match remember's lowercase storage
        if (!string.IsNullOrWhiteSpace(category))
            category = category.ToLowerInvariant();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

            var now = DateTime.UtcNow;
            List<AgentMemoryEntity> memories;

            if (!string.IsNullOrWhiteSpace(query))
            {
                memories = await RecallHandler.SearchWithFts5Async(
                    db, _agentId, query, category, key);
            }
            else
            {
                var q = db.AgentMemories.Where(m => m.AgentId == _agentId || m.Category == "shared");

                if (!string.IsNullOrWhiteSpace(category))
                    q = q.Where(m => m.Category == category);
                if (!string.IsNullOrWhiteSpace(key))
                    q = q.Where(m => m.Key == key);

                memories = await q.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();
            }

            if (!includeExpired)
                memories = memories.Where(m => m.ExpiresAt == null || m.ExpiresAt > now).ToList();

            // Update LastAccessedAt for staleness tracking (best-effort, matching RecallHandler)
            if (memories.Count > 0)
            {
                try
                {
                    foreach (var group in memories.GroupBy(m => m.AgentId))
                    {
                        var keyList = group.Select(g => g.Key).Distinct().ToList();
                        var placeholders = string.Join(", ", keyList.Select((_, i) => $"{{{i + 2}}}"));
                        var sql = $"UPDATE agent_memories SET LastAccessedAt = {{0}} WHERE AgentId = {{1}} AND Key IN ({placeholders})";
                        var parameters = new List<object> { now, group.Key };
                        parameters.AddRange(keyList);
                        await db.Database.ExecuteSqlRawAsync(sql, parameters.ToArray());
                    }
                }
                catch { /* LastAccessedAt update is best-effort */ }
            }

            if (memories.Count == 0)
                return "No memories found.";

            var lines = memories.Select(m =>
            {
                var line = $"- [{m.Category}] {m.Key}: {m.Value}";
                if (m.Category == "shared" && m.AgentId != _agentId)
                    line += $" (from {m.AgentId})";
                if (RecallHandler.IsStale(m, now))
                    line += " ⚠️ stale";
                if (m.ExpiresAt.HasValue)
                    line += $" (expires: {m.ExpiresAt.Value:u})";
                return line;
            });

            return $"Memories ({memories.Count}):\n{string.Join('\n', lines)}";
        }
        catch (Exception ex)
        {
            return $"Error recalling memories: {ex.Message}";
        }
    }
}
