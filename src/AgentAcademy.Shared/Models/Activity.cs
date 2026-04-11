namespace AgentAcademy.Shared.Models;

/// <summary>
/// An event emitted during collaboration, providing an audit trail of
/// agent actions, room state changes, and system events.
/// </summary>
public record ActivityEvent(
    string Id,
    ActivityEventType Type,
    ActivitySeverity Severity,
    string? RoomId,
    string? ActorId,
    string? TaskId,
    string Message,
    string? CorrelationId,
    DateTime OccurredAt,
    Dictionary<string, object?>? Metadata = null
);

/// <summary>
/// Top-level overview of the entire workspace: configured agents, active rooms,
/// recent activity, and agent locations. Served to the dashboard.
/// </summary>
public record WorkspaceOverview(
    List<AgentDefinition> ConfiguredAgents,
    List<RoomSnapshot> Rooms,
    List<ActivityEvent> RecentActivity,
    List<AgentLocation> AgentLocations,
    List<BreakoutRoom> BreakoutRooms,
    DateTime GeneratedAt
);
