namespace AgentAcademy.Shared.Models;

/// <summary>
/// Git identity for agent commit attribution.
/// </summary>
public record AgentGitIdentity(
    string AuthorName,
    string AuthorEmail
);

/// <summary>
/// Defines an agent's identity, capabilities, and configuration.
/// Loaded from the agent catalog at startup.
/// </summary>
public record AgentDefinition(
    string Id,
    string Name,
    string Role,
    string Summary,
    string StartupPrompt,
    string? Model,
    List<string> CapabilityTags,
    List<string> EnabledTools,
    bool AutoJoinDefaultRoom,
    AgentGitIdentity? GitIdentity = null
);

/// <summary>
/// Real-time presence information for an agent in a room.
/// </summary>
public record AgentPresence(
    string AgentId,
    string Name,
    string Role,
    AgentAvailability Availability,
    bool IsPreferred,
    DateTime LastActivityAt,
    List<string> ActiveCapabilities
);

/// <summary>
/// Tracks an agent's current physical location and state within the workspace.
/// </summary>
public record AgentLocation(
    string AgentId,
    string RoomId,
    AgentState State,
    string? BreakoutRoomId,
    DateTime UpdatedAt
);

/// <summary>
/// Configuration options for loading an agent catalog into a workspace,
/// including the default room assignment.
/// </summary>
public record AgentCatalogOptions(
    string DefaultRoomId,
    string DefaultRoomName,
    List<AgentDefinition> Agents
);
