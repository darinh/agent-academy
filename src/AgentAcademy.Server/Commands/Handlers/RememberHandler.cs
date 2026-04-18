using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles REMEMBER — upserts a memory entry for the executing agent.
/// Supports optional TTL (time-to-live) in hours for automatic expiration.
/// </summary>
public sealed class RememberHandler : ICommandHandler
{
    public string CommandName => "REMEMBER";
    public bool IsRetrySafe => true;

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

        if (value.Length > MemoryValueLimits.MaxValueChars)
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = $"value exceeds the {MemoryValueLimits.MaxValueChars}-character limit (received {value.Length}). Trim or split the memory." };

        if (!command.Args.TryGetValue("category", out var catObj) || catObj is not string category || string.IsNullOrWhiteSpace(category))
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = "Missing required argument: category" };

        if (!ValidCategories.Contains(category))
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = $"Invalid category '{category}'. Valid: {string.Join(", ", ValidCategories.Order())}" };

        // Parse optional TTL (in hours)
        int? ttlHours = null;
        bool ttlProvided = command.Args.ContainsKey("ttl");
        if (ttlProvided)
        {
            var ttlObj = command.Args["ttl"];
            if (ttlObj is string ttlStr && int.TryParse(ttlStr, out var parsed) && parsed > 0 && parsed <= 87600)
                ttlHours = parsed;
            else if (ttlObj is int intVal && intVal > 0 && intVal <= 87600)
                ttlHours = intVal;
            else if (ttlObj is long longVal && longVal > 0 && longVal <= 87600)
                ttlHours = (int)longVal;
            else
                return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = "ttl must be a positive integer (hours, max 87600 = ~10 years)" };
        }

        // Normalize category to lowercase for consistent matching (ValidCategories is case-insensitive
        // but downstream shared-memory checks use exact string comparison)
        category = category.ToLowerInvariant();

        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        var existing = await db.AgentMemories.FindAsync(context.AgentId, key);
        var now = DateTime.UtcNow;
        DateTime? expiresAt = ttlHours.HasValue ? now.AddHours(ttlHours.Value) : null;

        if (existing != null)
        {
            existing.Category = category;
            existing.Value = value;
            existing.UpdatedAt = now;
            // Only overwrite ExpiresAt when TTL is explicitly provided
            if (ttlProvided)
                existing.ExpiresAt = expiresAt;
        }
        else
        {
            db.AgentMemories.Add(new AgentMemoryEntity
            {
                AgentId = context.AgentId,
                Key = key,
                Category = category,
                Value = value,
                CreatedAt = now,
                ExpiresAt = expiresAt
            });
        }

        await db.SaveChangesAsync();

        var result = new Dictionary<string, object?>
        {
            ["key"] = key,
            ["category"] = category,
            ["action"] = existing != null ? "updated" : "created"
        };
        if (expiresAt.HasValue)
            result["expiresAt"] = expiresAt.Value.ToString("o");

        return command with
        {
            Status = CommandStatus.Success,
            Result = result
        };
    }
}
