using System;

namespace AgentAcademy.Server.Models;

/// <summary>
/// Represents a single tool invocation captured for audit purposes.
/// </summary>
public sealed record ToolAuditEntry
{
    /// <summary>
    /// Gets the name of the tool that was invoked.
    /// </summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the unique identifier of the agent that invoked the tool.
    /// </summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the display name of the agent that invoked the tool.
    /// </summary>
    public string AgentName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the UTC timestamp when the tool was invoked.
    /// </summary>
    public DateTime InvokedAt { get; init; }

    /// <summary>
    /// Gets the file path targeted by the invocation when the tool operates on a file.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the tool invocation completed successfully.
    /// </summary>
    public bool Succeeded { get; init; }
}
