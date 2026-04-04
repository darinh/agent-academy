using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles LIST_MEMORIES — lists all of the agent's memories, optionally filtered by category.
/// </summary>
public sealed class ListMemoriesHandler : ICommandHandler
{
    public string CommandName => "LIST_MEMORIES";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        // Include own memories + shared memories from all agents
        var query = db.AgentMemories.Where(m => m.AgentId == context.AgentId || m.Category == "shared");

        if (command.Args.TryGetValue("category", out var catObj) && catObj is string category && !string.IsNullOrWhiteSpace(category))
            query = query.Where(m => m.Category == category);

        var memories = await query.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();

        var result = memories.Select(m => new Dictionary<string, object?>
        {
            ["category"] = m.Category,
            ["key"] = m.Key,
            ["value"] = m.Value,
            ["createdAt"] = m.CreatedAt.ToString("o"),
            ["updatedAt"] = m.UpdatedAt?.ToString("o"),
            ["agentId"] = m.Category == "shared" && m.AgentId != context.AgentId ? m.AgentId : null
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?> { ["memories"] = result, ["count"] = result.Count }
        };
    }
}
