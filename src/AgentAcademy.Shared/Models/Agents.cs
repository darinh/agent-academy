namespace AgentAcademy.Shared.Models;

/// <summary>
/// Git identity for agent commit attribution.
/// </summary>
public record AgentGitIdentity(
    string AuthorName,
    string AuthorEmail
);

/// <summary>
/// Per-agent resource limits. Null values mean unlimited.
/// Token/cost quotas are best-effort (checked pre-call against recent DB records;
/// concurrent calls or large responses may slightly overshoot).
/// </summary>
public record ResourceQuota(
    int? MaxRequestsPerHour,
    long? MaxTokensPerHour,
    decimal? MaxCostPerHour
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
    AgentGitIdentity? GitIdentity = null,
    CommandPermissionSet? Permissions = null,
    ResourceQuota? Quota = null
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
/// including the default room assignment. Implements <see cref="IAgentCatalog"/>
/// so test code can pass catalog options directly where the interface is expected.
/// </summary>
public record AgentCatalogOptions(
    string DefaultRoomId,
    string DefaultRoomName,
    List<AgentDefinition> Agents
) : IAgentCatalog
{
    IReadOnlyList<AgentDefinition> IAgentCatalog.Agents => Agents;
}

/// <summary>
/// Read-only view of the agent catalog. Consumers inject this interface
/// to get the current agent list. The underlying data may be swapped
/// at runtime when the catalog is hot-reloaded.
/// </summary>
public interface IAgentCatalog
{
    string DefaultRoomId { get; }
    string DefaultRoomName { get; }
    IReadOnlyList<AgentDefinition> Agents { get; }
}
