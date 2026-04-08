using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles IMPORT_MEMORIES — bulk upserts memories from a JSON payload.
/// Expects a "memories" arg containing a serialized JSON array of objects with
/// category, key, and value fields. Validates each entry against allowed categories.
/// </summary>
public sealed class ImportMemoriesHandler : ICommandHandler
{
    public string CommandName => "IMPORT_MEMORIES";

    internal const int MaxEntries = 500;
    private const int MaxReportedErrors = 50;

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("memories", out var memoriesObj) || memoriesObj is not string memoriesJson || string.IsNullOrWhiteSpace(memoriesJson))
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = "Missing required argument: memories (JSON array of {category, key, value})" };

        List<MemoryImportEntry>? entries;
        try
        {
            entries = System.Text.Json.JsonSerializer.Deserialize<List<MemoryImportEntry>>(
                memoriesJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException ex)
        {
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = $"Invalid JSON: {ex.Message}" };
        }

        if (entries is null || entries.Count == 0)
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = "Empty or null memories array" };

        if (entries.Count > MaxEntries)
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Validation, Error = $"Import capped at {MaxEntries} entries per request. Got {entries.Count}." };

        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();
        var now = DateTime.UtcNow;
        int created = 0, updated = 0, skipped = 0;
        var errors = new List<string>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value) || string.IsNullOrWhiteSpace(entry.Category))
            {
                skipped++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add($"Skipped entry with missing key/value/category");
                continue;
            }

            var category = entry.Category.ToLowerInvariant();
            if (!RememberHandler.ValidCategories.Contains(category))
            {
                skipped++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add($"Skipped '{entry.Key}': invalid category '{entry.Category}'");
                continue;
            }

            if (entry.Value.Length > 500)
            {
                skipped++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add($"Skipped '{entry.Key}': value exceeds 500 character limit ({entry.Value.Length} chars)");
                continue;
            }

            if (entry.Ttl.HasValue && (entry.Ttl.Value <= 0 || entry.Ttl.Value > 87600))
            {
                skipped++;
                if (errors.Count < MaxReportedErrors)
                    errors.Add($"Skipped '{entry.Key}': ttl must be 1-87600 hours");
                continue;
            }

            var existing = await db.AgentMemories.FindAsync(context.AgentId, entry.Key);
            var expiresAt = entry.Ttl is > 0 ? (DateTime?)now.AddHours(entry.Ttl.Value) : null;
            if (existing != null)
            {
                existing.Category = category;
                existing.Value = entry.Value;
                existing.UpdatedAt = now;
                // Only overwrite ExpiresAt when TTL is explicitly provided
                if (entry.Ttl.HasValue)
                    existing.ExpiresAt = expiresAt;
                updated++;
            }
            else
            {
                db.AgentMemories.Add(new AgentMemoryEntity
                {
                    AgentId = context.AgentId,
                    Key = entry.Key,
                    Category = category,
                    Value = entry.Value,
                    CreatedAt = now,
                    ExpiresAt = expiresAt,
                });
                created++;
            }
        }

        await db.SaveChangesAsync();

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["created"] = created,
                ["updated"] = updated,
                ["skipped"] = skipped,
                ["total"] = entries.Count,
                ["errors"] = errors.Count > 0 ? errors : null,
            }
        };
    }

    internal record MemoryImportEntry(string Category, string Key, string Value, int? Ttl = null);
}
