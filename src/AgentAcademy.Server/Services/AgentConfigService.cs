using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Merges catalog agent definitions with DB-backed configuration overrides
/// to produce effective agent definitions used by the orchestrator and executor.
///
/// Layering order:
///   1. StartupPromptOverride ?? catalog StartupPrompt
///   2. + InstructionTemplate.Content (if template assigned)
///   3. + CustomInstructions (if set)
/// </summary>
public sealed class AgentConfigService
{
    private readonly AgentAcademyDbContext _db;

    public AgentConfigService(AgentAcademyDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the effective agent definition with DB overrides merged into catalog defaults.
    /// If no override exists, returns the catalog agent unchanged.
    /// </summary>
    public async Task<AgentDefinition> GetEffectiveAgentAsync(AgentDefinition catalogAgent)
    {
        var config = await _db.AgentConfigs
            .Include(c => c.InstructionTemplate)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentId == catalogAgent.Id);

        if (config is null)
            return catalogAgent;

        return MergeAgent(catalogAgent, config);
    }

    /// <summary>
    /// Returns effective agent definitions for all provided catalog agents.
    /// Loads all overrides in a single query for efficiency.
    /// </summary>
    public async Task<List<AgentDefinition>> GetEffectiveAgentsAsync(
        IEnumerable<AgentDefinition> catalogAgents)
    {
        var agentList = catalogAgents.ToList();
        var agentIds = agentList.Select(a => a.Id).ToList();

        var configs = await _db.AgentConfigs
            .Include(c => c.InstructionTemplate)
            .AsNoTracking()
            .Where(c => agentIds.Contains(c.AgentId))
            .ToDictionaryAsync(c => c.AgentId);

        return agentList
            .Select(agent => configs.TryGetValue(agent.Id, out var config)
                ? MergeAgent(agent, config)
                : agent)
            .ToList();
    }

    /// <summary>
    /// Merges a single catalog agent with its DB override to produce an effective definition.
    /// </summary>
    internal static AgentDefinition MergeAgent(
        AgentDefinition catalogAgent,
        Data.Entities.AgentConfigEntity config)
    {
        var effectiveModel = !string.IsNullOrWhiteSpace(config.ModelOverride)
            ? config.ModelOverride
            : catalogAgent.Model;

        var effectivePrompt = BuildEffectivePrompt(
            catalogAgent.StartupPrompt,
            config.StartupPromptOverride,
            config.InstructionTemplate?.Content,
            config.CustomInstructions);

        return catalogAgent with
        {
            StartupPrompt = effectivePrompt,
            Model = effectiveModel
        };
    }

    /// <summary>
    /// Builds the layered effective prompt from catalog default + override + template + custom.
    /// </summary>
    internal static string BuildEffectivePrompt(
        string catalogPrompt,
        string? startupOverride,
        string? templateContent,
        string? customInstructions)
    {
        var basePrompt = !string.IsNullOrWhiteSpace(startupOverride)
            ? startupOverride
            : catalogPrompt;

        var parts = new List<string> { basePrompt };

        if (!string.IsNullOrWhiteSpace(templateContent))
            parts.Add(templateContent);

        if (!string.IsNullOrWhiteSpace(customInstructions))
            parts.Add(customInstructions);

        return string.Join("\n\n", parts);
    }

    // ── Agent Config CRUD ──────────────────────────────────────

    /// <summary>
    /// Returns the raw DB override for an agent, or null if none exists.
    /// Includes the navigation to InstructionTemplate for template name resolution.
    /// </summary>
    public async Task<Data.Entities.AgentConfigEntity?> GetConfigOverrideAsync(string agentId)
    {
        return await _db.AgentConfigs
            .Include(c => c.InstructionTemplate)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentId == agentId);
    }

    /// <summary>
    /// Creates or updates an agent configuration override.
    /// All override fields are nullable — null clears that field.
    /// </summary>
    public async Task<Data.Entities.AgentConfigEntity> UpsertConfigAsync(
        string agentId,
        string? startupPromptOverride,
        string? modelOverride,
        string? customInstructions,
        string? instructionTemplateId)
    {
        // Validate template exists if provided
        if (instructionTemplateId is not null)
        {
            var templateExists = await _db.InstructionTemplates
                .AnyAsync(t => t.Id == instructionTemplateId);
            if (!templateExists)
                throw new ArgumentException($"Instruction template '{instructionTemplateId}' not found.");
        }

        var existing = await _db.AgentConfigs
            .FirstOrDefaultAsync(c => c.AgentId == agentId);

        if (existing is not null)
        {
            existing.StartupPromptOverride = startupPromptOverride;
            existing.ModelOverride = modelOverride;
            existing.CustomInstructions = customInstructions;
            existing.InstructionTemplateId = instructionTemplateId;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new Data.Entities.AgentConfigEntity
            {
                AgentId = agentId,
                StartupPromptOverride = startupPromptOverride,
                ModelOverride = modelOverride,
                CustomInstructions = customInstructions,
                InstructionTemplateId = instructionTemplateId,
                UpdatedAt = DateTime.UtcNow
            };
            _db.AgentConfigs.Add(existing);
        }

        await _db.SaveChangesAsync();

        // Re-fetch with template navigation loaded
        return (await GetConfigOverrideAsync(agentId))!;
    }

    /// <summary>
    /// Deletes the configuration override for an agent, reverting to catalog defaults.
    /// Returns true if an override existed and was deleted.
    /// </summary>
    public async Task<bool> DeleteConfigAsync(string agentId)
    {
        var existing = await _db.AgentConfigs
            .FirstOrDefaultAsync(c => c.AgentId == agentId);

        if (existing is null)
            return false;

        _db.AgentConfigs.Remove(existing);
        await _db.SaveChangesAsync();
        return true;
    }

    // ── Instruction Template CRUD ──────────────────────────────

    /// <summary>
    /// Returns all instruction templates ordered by name.
    /// </summary>
    public async Task<List<Data.Entities.InstructionTemplateEntity>> GetAllTemplatesAsync()
    {
        return await _db.InstructionTemplates
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Returns a single instruction template by ID, or null if not found.
    /// </summary>
    public async Task<Data.Entities.InstructionTemplateEntity?> GetTemplateAsync(string id)
    {
        return await _db.InstructionTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    /// <summary>
    /// Creates a new instruction template. Throws if name is already taken.
    /// </summary>
    public async Task<Data.Entities.InstructionTemplateEntity> CreateTemplateAsync(
        string name, string? description, string content)
    {
        var nameExists = await _db.InstructionTemplates
            .AnyAsync(t => t.Name == name);
        if (nameExists)
            throw new InvalidOperationException($"An instruction template named '{name}' already exists.");

        var now = DateTime.UtcNow;
        var template = new Data.Entities.InstructionTemplateEntity
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Description = description,
            Content = content,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.InstructionTemplates.Add(template);
        await _db.SaveChangesAsync();
        return template;
    }

    /// <summary>
    /// Updates an existing instruction template. Returns null if not found.
    /// Throws if the new name conflicts with another template.
    /// </summary>
    public async Task<Data.Entities.InstructionTemplateEntity?> UpdateTemplateAsync(
        string id, string name, string? description, string content)
    {
        var template = await _db.InstructionTemplates
            .FirstOrDefaultAsync(t => t.Id == id);

        if (template is null)
            return null;

        // Check for name conflict with a different template
        var nameConflict = await _db.InstructionTemplates
            .AnyAsync(t => t.Name == name && t.Id != id);
        if (nameConflict)
            throw new InvalidOperationException($"An instruction template named '{name}' already exists.");

        template.Name = name;
        template.Description = description;
        template.Content = content;
        template.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return template;
    }

    /// <summary>
    /// Deletes an instruction template. FK SetNull cascades to agent_configs.
    /// Returns true if the template existed and was deleted.
    /// </summary>
    public async Task<bool> DeleteTemplateAsync(string id)
    {
        var template = await _db.InstructionTemplates
            .FirstOrDefaultAsync(t => t.Id == id);

        if (template is null)
            return false;

        _db.InstructionTemplates.Remove(template);
        await _db.SaveChangesAsync();
        return true;
    }
}
