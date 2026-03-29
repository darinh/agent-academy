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
}
