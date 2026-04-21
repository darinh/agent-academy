using AgentAcademy.Forge.Execution;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Maps forge queue/progress lifecycle signals to ActivityEvent broadcasts.
/// </summary>
public sealed class ForgeActivityEventAdapter : IForgeActivityEventAdapter
{
    private readonly IActivityBroadcaster _activityBus;

    public ForgeActivityEventAdapter(IActivityBroadcaster activityBus)
    {
        _activityBus = activityBus;
    }

    public void PublishJobQueued(string jobId, string message)
        => BroadcastForgeEvent(ActivityEventType.ForgeJobQueued, jobId, message);

    public void PublishJobStarted(string jobId, string message)
        => BroadcastForgeEvent(ActivityEventType.ForgeJobStarted, jobId, message);

    public void PublishJobFinished(string jobId, string message, string outcome, string runId)
    {
        var type = outcome == "succeeded"
            ? ActivityEventType.ForgeJobCompleted
            : ActivityEventType.ForgeJobFailed;

        BroadcastForgeEvent(type, jobId, message, new Dictionary<string, object?>
        {
            ["runId"] = runId,
            ["outcome"] = outcome
        });
    }

    public void PublishProgress(string jobId, ForgeProgressEvent evt)
    {
        var phaseId = evt.PhaseId ?? "unknown";
        var (eventType, message) = evt.Kind switch
        {
            ForgeProgressKind.WaveStarted => (ActivityEventType.ForgePhaseStarted, evt.Message ?? "Wave started"),
            ForgeProgressKind.PhaseCompleted => (ActivityEventType.ForgePhaseCompleted, $"Phase {phaseId} completed"),
            ForgeProgressKind.PhaseFailed => (ActivityEventType.ForgePhaseFailed, $"Phase {phaseId} failed"),
            ForgeProgressKind.PhaseStarted => (ActivityEventType.ForgePhaseStarted, $"Phase {phaseId} started"),
            _ => (default(ActivityEventType?), (string?)null)
        };

        if (eventType is null)
            return;

        BroadcastForgeEvent(eventType.Value, jobId, message!, new Dictionary<string, object?>
        {
            ["runId"] = evt.RunId,
            ["phaseId"] = evt.PhaseId,
            ["wave"] = evt.Wave,
            ["kind"] = evt.Kind.ToString()
        });
    }

    private void BroadcastForgeEvent(
        ActivityEventType type,
        string jobId,
        string message,
        Dictionary<string, object?>? metadata = null)
    {
        metadata ??= new Dictionary<string, object?>();
        metadata["jobId"] = jobId;

        _activityBus.Broadcast(new ActivityEvent(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            Severity: ActivitySeverity.Info,
            RoomId: null,
            ActorId: null,
            TaskId: null,
            Message: message,
            CorrelationId: jobId,
            OccurredAt: DateTime.UtcNow,
            Metadata: metadata
        ));
    }
}
