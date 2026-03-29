namespace AgentAcademy.Server.Data.Entities;

/// <summary>
/// Reusable instruction template that can be applied to any agent.
/// Templates provide common instruction patterns (e.g., "Verification-First", "Pushback-Enabled").
/// </summary>
public class InstructionTemplateEntity
{
    public string Id { get; set; } = "";

    /// <summary>
    /// Human-readable name (unique). Displayed in UI template picker.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Optional description of what this template does.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The actual instruction text appended to the agent's startup prompt.
    /// </summary>
    public string Content { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
