using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Queue-based message processor that serializes agent work. Accepts human
/// messages and DMs, enqueues them, and dispatches to <see cref="IConversationRoundRunner"/>
/// or <see cref="IDirectMessageRouter"/> for execution. All conversation and
/// DM logic has been extracted into those dedicated services.
/// </summary>
public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConversationRoundRunner _roundRunner;
    private readonly IDirectMessageRouter _dmRouter;
    private readonly IBreakoutLifecycleService _breakoutLifecycle;
    private readonly ILogger<AgentOrchestrator> _logger;

    private readonly Queue<QueueItem> _queue = new();
    private readonly HashSet<string> _queuedDirectMessages = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private bool _processing;
    private readonly CancellationTokenSource _cts = new();

    private record QueueItem(string RoomId, string? TargetAgentId = null);

    /// <summary>Returns the current number of items in the processing queue (for testing/diagnostics).</summary>
    internal int QueueDepth { get { lock (_lock) { return _queue.Count; } } }

    public AgentOrchestrator(
        IServiceScopeFactory scopeFactory,
        IConversationRoundRunner roundRunner,
        IDirectMessageRouter dmRouter,
        IBreakoutLifecycleService breakoutLifecycle,
        ILogger<AgentOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _roundRunner = roundRunner;
        _dmRouter = dmRouter;
        _breakoutLifecycle = breakoutLifecycle;
        _logger = logger;
    }

    /// <summary>Signals the orchestrator to stop processing.</summary>
    public void Stop()
    {
        _cts.Cancel();
        _breakoutLifecycle.Stop();
    }

    public async Task HandleStartupRecoveryAsync(string mainRoomId)
    {
        if (!CrashRecoveryService.CurrentCrashDetected)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var crashRecovery = scope.ServiceProvider.GetRequiredService<ICrashRecoveryService>();
        var result = await crashRecovery.RecoverFromCrashAsync(mainRoomId);

        _logger.LogWarning(
            "Startup crash recovery ran for main room {RoomId}: {BreakoutCount} breakouts closed, {AgentCount} lingering agents reset",
            mainRoomId, result.ClosedBreakoutRooms, result.ResetWorkingAgents);
    }

    /// <summary>
    /// Scans for rooms with unanswered human messages and re-enqueues them.
    /// Call on every startup to recover queue state lost during shutdown or crash.
    /// </summary>
    public async Task ReconstructQueueAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
        var pendingRoomIds = await roomService.GetRoomsWithPendingHumanMessagesAsync();

        if (pendingRoomIds.Count == 0)
        {
            _logger.LogInformation("Queue reconstruction: no rooms with pending human messages");
            return;
        }

        lock (_lock)
        {
            foreach (var roomId in pendingRoomIds)
            {
                _queue.Enqueue(new QueueItem(roomId));
            }
        }

        _logger.LogInformation(
            "Queue reconstruction: re-enqueued {Count} room(s) with pending human messages: {RoomIds}",
            pendingRoomIds.Count, string.Join(", ", pendingRoomIds));

        SignalProcessing();
    }

    // ── PUBLIC ENTRY POINT ──────────────────────────────────────

    /// <summary>
    /// Enqueues a room for processing after a human message arrives.
    /// Processing is serialized — only one room is handled at a time.
    /// </summary>
    public void HandleHumanMessage(string roomId)
    {
        EnqueueAndProcess(new QueueItem(roomId));
    }

    /// <summary>
    /// Triggers an immediate round for a specific agent after receiving a DM.
    /// Finds the agent's current room and runs only that agent.
    /// </summary>
    public void HandleDirectMessage(string recipientAgentId)
    {
        if (!TryEnqueueDirectMessage(recipientAgentId))
        {
            return;
        }

        SignalProcessing();
    }

    // ── QUEUE ───────────────────────────────────────────────────

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                QueueItem item;
                if (!TryDequeue(out item))
                {
                    break;
                }

                await ProcessQueueItemAsync(item);
            }
        }
        finally
        {
            EndProcessingAndRestartIfNeeded();
        }
    }

    private void EnqueueAndProcess(QueueItem item)
    {
        lock (_lock) { _queue.Enqueue(item); }
        SignalProcessing();
    }

    private bool TryEnqueueDirectMessage(string recipientAgentId)
    {
        lock (_lock)
        {
            // Dedupe: skip if a DM trigger for this agent is already queued.
            if (!_queuedDirectMessages.Add(recipientAgentId))
            {
                return false;
            }

            _queue.Enqueue(new QueueItem(RoomId: string.Empty, TargetAgentId: recipientAgentId));
            return true;
        }
    }

    private bool TryDequeue(out QueueItem item)
    {
        lock (_lock)
        {
            if (_queue.TryDequeue(out var dequeuedItem))
            {
                if (dequeuedItem.TargetAgentId is { } targetAgentId)
                {
                    _queuedDirectMessages.Remove(targetAgentId);
                }

                item = dequeuedItem;
                return true;
            }

            item = default!;
            return false;
        }
    }

    private async Task ProcessQueueItemAsync(QueueItem item)
    {
        try
        {
            if (item.TargetAgentId is { } targetAgentId)
            {
                await _dmRouter.RouteAsync(targetAgentId);
            }
            else
            {
                await _roundRunner.RunRoundsAsync(item.RoomId, _cts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orchestrator failed for {Item}", item);
        }
    }

    private void EndProcessingAndRestartIfNeeded()
    {
        var shouldRestart = false;

        lock (_lock)
        {
            _processing = false;

            if (!_cts.IsCancellationRequested && _queue.Count > 0)
            {
                _processing = true;
                shouldRestart = true;
            }
        }

        if (shouldRestart)
        {
            _ = ProcessQueueAsync();
        }
    }

    private void SignalProcessing()
    {
        var shouldStart = false;

        lock (_lock)
        {
            if (!_processing && !_cts.IsCancellationRequested && _queue.Count > 0)
            {
                _processing = true;
                shouldStart = true;
            }
        }

        if (shouldStart)
        {
            _ = ProcessQueueAsync();
        }
    }
}
