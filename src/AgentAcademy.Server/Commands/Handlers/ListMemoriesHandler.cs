using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_MEMORIES — lists all of the agent's memories, optionally filtered by category.
/// Filters out expired memories and updates LastAccessedAt on returned results.
/// </summary>
public sealed class ListMemoriesHandler : ICommandHandler
{
    public string CommandName => "LIST_MEMORIES";
    public bool IsRetrySafe => true;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        var now = DateTime.UtcNow;

        // Include own memories + shared memories from all agents
        var query = db.AgentMemories.Where(m => m.AgentId == context.AgentId || m.Category == "shared");

        if (command.Args.TryGetValue("category", out var catObj) && catObj is string category && !string.IsNullOrWhiteSpace(category))
            query = query.Where(m => m.Category == category);

        // Check for include_expired flag
        var includeExpired = command.Args.TryGetValue("include_expired", out var expObj) &&
            (expObj is true || (expObj is string expStr && expStr.Equals("true", StringComparison.OrdinalIgnoreCase)));

        if (!includeExpired)
            query = query.Where(m => m.ExpiresAt == null || m.ExpiresAt > now);

        var memories = await query.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();

        // Update LastAccessedAt for returned memories (best-effort, batched)
        if (memories.Count > 0)
        {
            try
            {
                foreach (var group in memories.GroupBy(m => m.AgentId))
                {
                    var agentId = group.Key;
                    var keyList = group.Select(g => g.Key).ToList();
                    var placeholders = string.Join(", ", keyList.Select((_, i) => $"{{{i + 2}}}"));
                    var sql = $"UPDATE agent_memories SET LastAccessedAt = {{0}} WHERE AgentId = {{1}} AND Key IN ({placeholders})";
                    var parameters = new List<object> { now, agentId };
                    parameters.AddRange(keyList);
                    await db.Database.ExecuteSqlRawAsync(sql, parameters.ToArray());
                }
            }
            catch { /* best-effort */ }
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
            ["stale"] = RecallHandler.IsStale(m, now) ? true : null
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?> { ["memories"] = result, ["count"] = result.Count }
        };
    }
}
