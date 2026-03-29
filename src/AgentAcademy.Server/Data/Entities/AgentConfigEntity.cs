namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Per-agent configuration overrides stored in the database.
/// Merges with catalog defaults via AgentConfigService to produce effective agent definitions.
/// </summary>
public class AgentConfigEntity
{
    /// <summary>
    /// Matches an agent's catalog ID (e.g., "planner-1", "architect-1").
    /// </summary>
    public string AgentId { get; set; } = "";

    /// <summary>
    /// If set, replaces the agent's catalog StartupPrompt entirely.
    /// </summary>
    public string? StartupPromptOverride { get; set; }

    /// <summary>
    /// If set, replaces the agent's catalog Model (e.g., "claude-opus-4.6" → "gpt-5.4").
    /// </summary>
    public string? ModelOverride { get; set; }

    /// <summary>
    /// Free-form instructions appended after the startup prompt and instruction template.
    /// </summary>
    public string? CustomInstructions { get; set; }

    /// <summary>
    /// Optional FK to an instruction template whose content is appended after the startup prompt.
    /// </summary>
    public string? InstructionTemplateId { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Navigation
    public InstructionTemplateEntity? InstructionTemplate { get; set; }
}
