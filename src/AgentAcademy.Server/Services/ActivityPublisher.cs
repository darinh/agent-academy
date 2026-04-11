using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Creates, persists (via EF tracker), and broadcasts activity events.
/// Extracted from WorkspaceRuntime and TaskLifecycleService to eliminate
/// duplicated Publish() logic. Caller must call SaveChangesAsync on the
/// DbContext to commit the persisted entity.
/// </summary>
public sealed class ActivityPublisher
{
    private readonly AgentAcademyDbContext _db;
    private readonly ActivityBroadcaster _activityBus;

    public ActivityPublisher(AgentAcademyDbContext db, ActivityBroadcaster activityBus)
    {
        _db = db;
        _activityBus = activityBus;
    }

    /// <summary>
    /// Creates an activity event, adds it to the EF change tracker, and broadcasts
    /// it to in-memory subscribers. The caller owns SaveChangesAsync.
    /// </summary>
    public ActivityEvent Publish(
        ActivityEventType type,
        string? roomId,
        string? actorId,
        string? taskId,
        string message,
        string? correlationId = null,
        ActivitySeverity severity = ActivitySeverity.Info)
    {
        var evt = new ActivityEvent(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            Severity: severity,
            RoomId: roomId,
            ActorId: actorId,
            TaskId: taskId,
            Message: message,
            CorrelationId: correlationId,
            OccurredAt: DateTime.UtcNow
        );

        _db.ActivityEvents.Add(new ActivityEventEntity
        {
            Id = evt.Id,
            Type = evt.Type.ToString(),
            Severity = evt.Severity.ToString(),
            RoomId = evt.RoomId,
            ActorId = evt.ActorId,
            TaskId = evt.TaskId,
            Message = evt.Message,
            CorrelationId = evt.CorrelationId,
            OccurredAt = evt.OccurredAt
        });

        _activityBus.Broadcast(evt);
        return evt;
    }

    /// <summary>
    /// Returns recent activity events from the in-memory buffer.
    /// </summary>
    public IReadOnlyList<ActivityEvent> GetRecentActivity()
        => _activityBus.GetRecentActivity();

    /// <summary>
    /// Subscribes to activity events. Returns an unsubscribe action.
    /// </summary>
    public Action Subscribe(Action<ActivityEvent> callback)
        => _activityBus.Subscribe(callback);
}
