using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Loads agent memories from the database, including shared memories and
/// expiry filtering. Updates LastAccessedAt timestamps on loaded entries.
/// Extracted from duplicated implementations in AgentOrchestrator and
/// BreakoutLifecycleService.
/// </summary>
public sealed class AgentMemoryLoader
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentMemoryLoader> _logger;

    public AgentMemoryLoader(
        IServiceScopeFactory scopeFactory,
        ILogger<AgentMemoryLoader> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<List<AgentMemory>> LoadAsync(string agentId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var now = DateTime.UtcNow;

            var entities = await db.AgentMemories
                .Where(m => m.AgentId == agentId || m.Category == "shared")
                .Where(m => m.ExpiresAt == null || m.ExpiresAt > now)
                .OrderBy(m => m.Category)
                .ThenBy(m => m.Key)
                .ToListAsync();

            // Update LastAccessedAt for all loaded memories (best-effort, batched)
            if (entities.Count > 0)
            {
                try
                {
                    foreach (var group in entities.GroupBy(e => e.AgentId))
                    {
                        var aid = group.Key;
                        var keyList = group.Select(g => g.Key).ToList();
                        var placeholders = string.Join(", ", keyList.Select((_, i) => $"{{{i + 2}}}"));
                        var sql = $"UPDATE agent_memories SET LastAccessedAt = {{0}} WHERE AgentId = {{1}} AND Key IN ({placeholders})";
                        var parameters = new List<object> { now, aid };
                        parameters.AddRange(keyList);
                        await db.Database.ExecuteSqlRawAsync(sql, parameters.ToArray());
                    }
                }
                catch { /* best-effort */ }
            }

            return entities.Select(e => new AgentMemory(
                e.AgentId, e.Category, e.Key, e.Value, e.CreatedAt, e.UpdatedAt,
                e.LastAccessedAt, e.ExpiresAt
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load memories for agent {AgentId}", agentId);
            return new List<AgentMemory>();
        }
    }
}
