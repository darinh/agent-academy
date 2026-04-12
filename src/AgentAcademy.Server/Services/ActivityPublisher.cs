using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Creates, persists (via EF tracker), and broadcasts activity events.
/// Caller must call SaveChangesAsync on the DbContext to commit the
/// persisted entity.
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
    /// Publishes an AgentThinking activity event and saves immediately.
    /// </summary>
    public async Task PublishThinkingAsync(AgentDefinition agent, string roomId)
    {
        Publish(ActivityEventType.AgentThinking, roomId, agent.Id, null,
            $"{agent.Name} is thinking...");
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Publishes an AgentFinished activity event and saves immediately.
    /// </summary>
    public async Task PublishFinishedAsync(AgentDefinition agent, string roomId)
    {
        Publish(ActivityEventType.AgentFinished, roomId, agent.Id, null,
            $"{agent.Name} finished.");
        await _db.SaveChangesAsync();
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
