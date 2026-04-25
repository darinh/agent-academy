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
    private readonly IOrchestratorDispatchService _dispatchService;
    private readonly IBreakoutLifecycleService _breakoutLifecycle;
    private readonly ILogger<AgentOrchestrator> _logger;

    private Queue<QueueItem> _queue = new();
    private readonly Dictionary<string, QueueItemKind> _queuedRoomKinds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _queuedDirectMessages = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private bool _processing;
    private readonly CancellationTokenSource _cts = new();

    private record QueueItem(
        string RoomId,
        string? TargetAgentId = null,
        QueueItemKind Kind = QueueItemKind.HumanMessage,
        // Captured at enqueue time for SystemContinuation items so the
        // dispatch path can re-check sprint state (blocked / completed /
        // awaiting sign-off) before running a stale continuation. Null
        // for HumanMessage and DM items — those gate elsewhere.
        string? SprintId = null);

    /// <summary>Returns the current number of items in the processing queue (for testing/diagnostics).</summary>
    internal int QueueDepth { get { lock (_lock) { return _queue.Count; } } }

    /// <summary>Test/diagnostics: peek at the kind of the queued item for a room, if any.</summary>
    internal QueueItemKind? PeekRoomKind(string roomId)
    {
        lock (_lock)
        {
            return _queuedRoomKinds.TryGetValue(roomId, out var kind) ? kind : null;
        }
    }

    public AgentOrchestrator(
        IServiceScopeFactory scopeFactory,
        IOrchestratorDispatchService dispatchService,
        IBreakoutLifecycleService breakoutLifecycle,
        ILogger<AgentOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _dispatchService = dispatchService;
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

        var enqueuedCount = EnqueueRooms(pendingRoomIds);

        _logger.LogInformation(
            "Queue reconstruction: enqueued {Count} room(s) with pending human messages: {RoomIds}",
            enqueuedCount, string.Join(", ", pendingRoomIds));

        StartProcessingIfNeeded();
    }

    // ── PUBLIC ENTRY POINT ──────────────────────────────────────

    /// <summary>
    /// Enqueues a room for processing after a human message arrives.
    /// Processing is serialized — only one room is handled at a time.
    /// </summary>
    public void HandleHumanMessage(string roomId)
    {
        if (!TryEnqueueRoom(roomId))
        {
            return;
        }

        StartProcessingIfNeeded();
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

        StartProcessingIfNeeded();
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

    private bool TryEnqueueRoom(string roomId)
    {
        lock (_lock)
        {
            return TryEnqueueRoomLocked(roomId, QueueItemKind.HumanMessage, sprintId: null);
        }
    }

    /// <summary>
    /// P1.2: Enqueue a self-drive continuation for the room. Honours the
    /// dedupe rules from p1-2-self-drive-design.md §4.4. Returns false if
    /// the continuation was dropped (e.g., a HumanMessage is already
    /// queued for the room — the human's trigger will run another
    /// conversation, and the post-round decision will re-evaluate).
    /// </summary>
    public bool TryEnqueueSystemContinuation(string roomId, string sprintId)
    {
        if (string.IsNullOrEmpty(roomId)) return false;
        if (string.IsNullOrEmpty(sprintId)) return false;

        bool enqueued;
        lock (_lock)
        {
            enqueued = TryEnqueueRoomLocked(roomId, QueueItemKind.SystemContinuation, sprintId);
        }

        if (enqueued) StartProcessingIfNeeded();
        return enqueued;
    }

    private int EnqueueRooms(IReadOnlyCollection<string> roomIds)
    {
        var enqueuedCount = 0;

        lock (_lock)
        {
            foreach (var roomId in roomIds)
            {
                if (!TryEnqueueRoomLocked(roomId, QueueItemKind.HumanMessage, sprintId: null))
                {
                    continue;
                }
                enqueuedCount++;
            }
        }

        return enqueuedCount;
    }

    private bool TryEnqueueDirectMessage(string recipientAgentId)
    {
        lock (_lock)
        {
            return TryEnqueueDirectMessageLocked(recipientAgentId);
        }
    }

    private bool TryDequeue(out QueueItem item)
    {
        lock (_lock)
        {
            if (_cts.IsCancellationRequested)
            {
                item = default!;
                return false;
            }

            if (_queue.TryDequeue(out var dequeuedItem))
            {
                if (dequeuedItem.TargetAgentId is { } targetAgentId)
                {
                    _queuedDirectMessages.Remove(targetAgentId);
                }
                else
                {
                    _queuedRoomKinds.Remove(dequeuedItem.RoomId);
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
            // P1.2: Stale SystemContinuation guard. A continuation may have
            // been enqueued before the sprint was blocked / completed /
            // cancelled / put into AwaitingSignOff. Honour design
            // principle 7 ("no round may run while BlockedAt != null") by
            // re-checking sprint state immediately before dispatch.
            // HumanMessage items don't need this gate — the message
            // service rejects writes to terminal-state rooms upstream.
            if (item.Kind == QueueItemKind.SystemContinuation
                && item.SprintId is { Length: > 0 } sprintId)
            {
                if (!await IsSystemContinuationStillEligibleAsync(sprintId))
                {
                    _logger.LogInformation(
                        "Skipping stale SystemContinuation for room {RoomId} (sprint {SprintId} no longer eligible)",
                        item.RoomId, sprintId);
                    return;
                }
            }

            await _dispatchService.DispatchAsync(
                item.RoomId, item.TargetAgentId, item.Kind, _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orchestrator failed for {Item}", item);
        }
    }

    private async Task<bool> IsSystemContinuationStillEligibleAsync(string sprintId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sprintService = scope.ServiceProvider.GetRequiredService<ISprintService>();
            var sprint = await sprintService.GetSprintByIdAsync(sprintId);
            if (sprint is null) return false;
            if (sprint.BlockedAt is not null) return false;
            if (sprint.AwaitingSignOff) return false;
            if (sprint.Status != "Active") return false;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SystemContinuation eligibility check failed for sprint {SprintId}; skipping dispatch",
                sprintId);
            return false;
        }
    }

    private void EndProcessingAndRestartIfNeeded()
    {
        lock (_lock)
        {
            _processing = false;
        }

        StartProcessingIfNeeded();
    }

    private void StartProcessingIfNeeded()
    {
        bool shouldStart;
        lock (_lock)
        {
            shouldStart = TryBeginProcessingLocked();
        }

        if (shouldStart)
        {
            _ = ProcessQueueAsync();
        }
    }

    private bool TryEnqueueRoomLocked(string roomId, QueueItemKind kind, string? sprintId)
    {
        // P1.2 §4.4 dedupe rules:
        //
        //                 NEW kind →
        //   EXISTING ↓    HumanMessage          SystemContinuation
        //   (none)        enqueue HM            enqueue SC
        //   HumanMessage  drop new (existing    drop new (a HM is already
        //                 already queued)        queued; running it will
        //                                        produce a fresh decision
        //                                        afterwards anyway)
        //   SystemCont.   upgrade in-place      drop new (one SC for this
        //                 to HumanMessage        room is enough — if the
        //                 (real human input      decision service runs
        //                 takes precedence;      again it will re-evaluate
        //                 same FIFO slot)        from fresh state)
        if (_queuedRoomKinds.TryGetValue(roomId, out var existingKind))
        {
            if (existingKind == QueueItemKind.SystemContinuation && kind == QueueItemKind.HumanMessage)
            {
                // Upgrade in-place: rewrite the queue with the matching
                // SystemContinuation entry promoted to HumanMessage.
                // Preserves FIFO order so the human is processed at the
                // same scheduling priority as the (pre-empted) continuation.
                UpgradeQueuedSystemContinuationToHumanMessageLocked(roomId);
                _queuedRoomKinds[roomId] = QueueItemKind.HumanMessage;
                return true;
            }
            // All other (existing, new) combinations: drop the new item.
            return false;
        }

        _queuedRoomKinds[roomId] = kind;
        _queue.Enqueue(new QueueItem(roomId, TargetAgentId: null, Kind: kind, SprintId: sprintId));
        return true;
    }

    private void UpgradeQueuedSystemContinuationToHumanMessageLocked(string roomId)
    {
        // Queue<T> doesn't support in-place mutation, so rebuild it.
        // Linear in queue depth; queue is small in practice (one item per
        // pending room). The match must be the FIRST SystemContinuation
        // for the room — there is at most one by construction (dedupe
        // already prevents two SCs for the same room).
        var rebuilt = new Queue<QueueItem>(_queue.Count);
        var upgraded = false;
        while (_queue.TryDequeue(out var item))
        {
            if (!upgraded
                && item.TargetAgentId is null
                && item.Kind == QueueItemKind.SystemContinuation
                && string.Equals(item.RoomId, roomId, StringComparison.Ordinal))
            {
                rebuilt.Enqueue(item with { Kind = QueueItemKind.HumanMessage, SprintId = null });
                upgraded = true;
            }
            else
            {
                rebuilt.Enqueue(item);
            }
        }
        _queue = rebuilt;
    }

    private bool TryEnqueueDirectMessageLocked(string recipientAgentId)
    {
        // Dedupe: skip if a DM trigger for this agent is already queued.
        if (!_queuedDirectMessages.Add(recipientAgentId))
        {
            return false;
        }

        _queue.Enqueue(new QueueItem(RoomId: string.Empty, TargetAgentId: recipientAgentId));
        return true;
    }

    private bool TryBeginProcessingLocked()
    {
        if (_processing || _cts.IsCancellationRequested || _queue.Count == 0)
        {
            return false;
        }

        _processing = true;
        return true;
    }
}
