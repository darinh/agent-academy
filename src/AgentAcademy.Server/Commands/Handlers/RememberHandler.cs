using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles REMEMBER — upserts a memory entry for the executing agent.
/// </summary>
public sealed class RememberHandler : ICommandHandler
{
    public string CommandName => "REMEMBER";

    internal static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "decision", "lesson", "pattern", "preference", "invariant", "risk",
        "gotcha", "incident", "constraint", "finding", "spec-drift",
        "mapping", "verification", "gap-pattern", "shared"
    };

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("key", out var keyObj) || keyObj is not string key || string.IsNullOrWhiteSpace(key))
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = "Missing required argument: key" };

        if (!command.Args.TryGetValue("value", out var valueObj) || valueObj is not string value || string.IsNullOrWhiteSpace(value))
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = "Missing required argument: value" };

        if (!command.Args.TryGetValue("category", out var catObj) || catObj is not string category || string.IsNullOrWhiteSpace(category))
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = "Missing required argument: category" };

        if (!ValidCategories.Contains(category))
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = $"Invalid category '{category}'. Valid: {string.Join(", ", ValidCategories.Order())}" };

        // Normalize category to lowercase for consistent matching (ValidCategories is case-insensitive
        // but downstream shared-memory checks use exact string comparison)
        category = category.ToLowerInvariant();

        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        var existing = await db.AgentMemories.FindAsync(context.AgentId, key);
        var now = DateTime.UtcNow;

        if (existing != null)
        {
            existing.Category = category;
            existing.Value = value;
            existing.UpdatedAt = now;
        }
        else
        {
            db.AgentMemories.Add(new AgentMemoryEntity
            {
                AgentId = context.AgentId,
                Key = key,
                Category = category,
                Value = value,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["key"] = key,
                ["category"] = category,
                ["action"] = existing != null ? "updated" : "created"
            }
        };
    }
}
