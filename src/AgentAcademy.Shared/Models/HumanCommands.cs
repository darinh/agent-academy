namespace AgentAcademy.Shared.Models;

/// <summary>
/// Metadata for a single form field in a human-executable command.
/// Drives the frontend's dynamic form rendering.
/// </summary>
public sealed record HumanCommandFieldMetadata(
    string Name,
    string Label,
    string Kind,
    string Description,
    string? Placeholder = null,
    bool Required = false,
    string? DefaultValue = null);

/// <summary>
/// Full metadata for a human-executable command, including UI hints
/// and argument schema. Served by GET /api/commands/metadata.
/// </summary>
public sealed record HumanCommandMetadata(
    string Command,
    string Title,
    string Category,
    string Description,
    string Detail,
    bool IsAsync,
    IReadOnlyList<HumanCommandFieldMetadata> Fields,
    bool IsDestructive = false,
    string? DestructiveWarning = null);
