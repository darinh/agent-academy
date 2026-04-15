using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Merges catalog agent definitions with DB-backed configuration overrides
/// and provides CRUD for agent config overrides and instruction templates.
/// </summary>
public interface IAgentConfigService
{
    // ── Effective Agent Resolution ────────────────────────────────

    /// <summary>
    /// Returns the effective agent definition with DB overrides merged into catalog defaults.
    /// If no override exists, returns the catalog agent unchanged.
    /// </summary>
    Task<AgentDefinition> GetEffectiveAgentAsync(AgentDefinition catalogAgent);

    /// <summary>
    /// Returns effective agent definitions for all provided catalog agents.
    /// Loads all overrides in a single query for efficiency.
    /// </summary>
    Task<List<AgentDefinition>> GetEffectiveAgentsAsync(IEnumerable<AgentDefinition> catalogAgents);

    // ── Agent Config CRUD ────────────────────────────────────────

    /// <summary>
    /// Returns the raw DB override for an agent, or null if none exists.
    /// Includes the navigation to InstructionTemplate for template name resolution.
    /// </summary>
    Task<AgentConfigEntity?> GetConfigOverrideAsync(string agentId);

    /// <summary>
    /// Creates or updates an agent configuration override.
    /// All override fields are nullable — null clears that field.
    /// </summary>
    Task<AgentConfigEntity> UpsertConfigAsync(
        string agentId,
        string? startupPromptOverride,
        string? modelOverride,
        string? customInstructions,
        string? instructionTemplateId);

    /// <summary>
    /// Deletes the configuration override for an agent, reverting to catalog defaults.
    /// Returns true if an override existed and was deleted.
    /// </summary>
    Task<bool> DeleteConfigAsync(string agentId);

    // ── Instruction Template CRUD ────────────────────────────────

    /// <summary>Returns all instruction templates ordered by name.</summary>
    Task<List<InstructionTemplateEntity>> GetAllTemplatesAsync();

    /// <summary>Returns a single instruction template by ID, or null if not found.</summary>
    Task<InstructionTemplateEntity?> GetTemplateAsync(string id);

    /// <summary>Creates a new instruction template. Throws if name is already taken.</summary>
    Task<InstructionTemplateEntity> CreateTemplateAsync(string name, string? description, string content);

    /// <summary>
    /// Updates an existing instruction template. Returns null if not found.
    /// Throws if the new name conflicts with another template.
    /// </summary>
    Task<InstructionTemplateEntity?> UpdateTemplateAsync(
        string id, string name, string? description, string content);

    /// <summary>
    /// Deletes an instruction template. FK SetNull cascades to agent_configs.
    /// Returns true if the template existed and was deleted.
    /// </summary>
    Task<bool> DeleteTemplateAsync(string id);
}
