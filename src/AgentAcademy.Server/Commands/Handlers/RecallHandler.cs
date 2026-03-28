using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RECALL — searches the agent's memories using LIKE patterns.
/// Supports filtering by category, key, or free-text query across key+value.
/// </summary>
public sealed class RecallHandler : ICommandHandler
{
    public string CommandName => "RECALL";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        var query = db.AgentMemories.Where(m => m.AgentId == context.AgentId);

        // Filter by category
        if (command.Args.TryGetValue("category", out var catObj) && catObj is string category && !string.IsNullOrWhiteSpace(category))
            query = query.Where(m => m.Category == category);

        // Filter by key
        if (command.Args.TryGetValue("key", out var keyObj) && keyObj is string key && !string.IsNullOrWhiteSpace(key))
            query = query.Where(m => m.Key == key);

        // Free-text search across key and value
        if (command.Args.TryGetValue("query", out var qObj) && qObj is string searchQuery && !string.IsNullOrWhiteSpace(searchQuery))
        {
            var pattern = $"%{searchQuery}%";
            query = query.Where(m =>
                EF.Functions.Like(m.Key, pattern) ||
                EF.Functions.Like(m.Value, pattern));
        }

        var memories = await query.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();

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
}
