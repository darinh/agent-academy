using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Creates, persists, and broadcasts activity events.
/// Caller must call SaveChangesAsync on the DbContext to commit
/// the persisted entity (except for convenience methods that save immediately).
/// </summary>
public interface IActivityPublisher
{
    /// <summary>
    /// Creates an activity event, adds it to the EF change tracker, and broadcasts
    /// it to in-memory subscribers. The caller owns SaveChangesAsync.
    /// </summary>
    ActivityEvent Publish(
        ActivityEventType type,
        string? roomId,
        string? actorId,
        string? taskId,
        string message,
        string? correlationId = null,
        ActivitySeverity severity = ActivitySeverity.Info);

    /// <summary>
    /// Publishes an AgentThinking activity event and saves immediately.
    /// </summary>
    Task PublishThinkingAsync(AgentDefinition agent, string roomId);

    /// <summary>
    /// Publishes an AgentFinished activity event and saves immediately.
    /// </summary>
    Task PublishFinishedAsync(AgentDefinition agent, string roomId);

    /// <summary>
    /// Returns recent activity events from the in-memory buffer.
    /// </summary>
    IReadOnlyList<ActivityEvent> GetRecentActivity();

    /// <summary>
    /// Subscribes to activity events. Returns an unsubscribe action.
    /// </summary>
    Action Subscribe(Action<ActivityEvent> callback);
}
