using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles EXPORT_MEMORIES — exports the agent's memories as a structured list,
/// optionally filtered by category. Used for backup, transfer, or inspection.
/// </summary>
public sealed class ExportMemoriesHandler : ICommandHandler
{
    public string CommandName => "EXPORT_MEMORIES";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        var query = db.AgentMemories.Where(m => m.AgentId == context.AgentId);

        if (command.Args.TryGetValue("category", out var catObj) && catObj is string category && !string.IsNullOrWhiteSpace(category))
            query = query.Where(m => m.Category == category.ToLowerInvariant());

        var memories = await query.OrderBy(m => m.Category).ThenBy(m => m.Key).ToListAsync();

        var exported = memories.Select(m => new Dictionary<string, object?>
        {
            ["category"] = m.Category,
            ["key"] = m.Key,
            ["value"] = m.Value,
            ["createdAt"] = m.CreatedAt.ToString("o"),
            ["updatedAt"] = m.UpdatedAt?.ToString("o"),
        }).ToList();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["memories"] = exported,
                ["count"] = exported.Count,
                ["agentId"] = context.AgentId,
            }
        };
    }
}
