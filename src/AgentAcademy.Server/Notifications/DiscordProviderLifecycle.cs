using System.Diagnostics;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// The legal lifecycle states of <see cref="DiscordNotificationProvider"/>.
/// See <c>specs/100-product-vision/discord-lifecycle-refactor-design.md</c> §4.1
/// for the full state machine.
/// </summary>
internal enum LifecycleState
{
    Created,
    Configured,
    Connecting,
    Connected,
    Disconnecting,
    Disposing,
    Disposed,
}

/// <summary>
/// Categorises operations gated by <see cref="DiscordProviderLifecycle.TryEnterOperation"/>.
/// </summary>
internal enum OperationKind
{
    /// <summary>Outbound message send. Requires <see cref="LifecycleState.Connected"/>.</summary>
    Send,

    /// <summary>Interactive input collection. Requires <see cref="LifecycleState.Connected"/>.</summary>
    RequestInput,

    /// <summary>Best-effort room lifecycle (rename / delete channel). Requires <see cref="LifecycleState.Configured"/> or later (excluding terminal teardown).</summary>
    RoomLifecycle,
}

/// <summary>
/// Finite state machine that owns <see cref="DiscordNotificationProvider"/>'s
/// lifecycle and concurrency invariants.
///
/// <para>
/// Replaces the previous Cartesian product of independent flags
/// (<c>_disposed</c>, <c>_config</c>, <c>_connection.IsConnected</c>,
/// <c>_drainTracker.IsTeardownInProgress</c>) with a single explicit
/// <see cref="LifecycleState"/> plus a transition table validated in code.
/// </para>
///
/// <para>
/// Locking discipline: all state transitions hold <see cref="_connectLock"/>;
/// state reads are lock-free against the volatile <see cref="_state"/>.
/// Lease objects are reference types with idempotent <c>Dispose</c> so that
/// double-release (e.g. via accidental copy / repeated dispose) cannot
/// over-release the lock or the drain tracker.
/// </para>
///
/// <para>
/// This type is <c>internal sealed</c> by design (decision E in the design doc):
/// tests drive it via <c>InternalsVisibleTo</c> and behavioural tests through
/// the public <see cref="DiscordNotificationProvider"/> surface.
/// </para>
/// </summary>
internal sealed class DiscordProviderLifecycle
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly OperationDrainTracker _drainTracker = new();

    // _state is mutated only under _connectLock; readers are lock-free via volatile.
    private volatile LifecycleState _state = LifecycleState.Created;

    // _config is mutated only under _connectLock; readers are lock-free via volatile.
    private volatile DiscordProviderConfig? _config;

    // Single-winner dispose gate. Set BEFORE waiting on _connectLock so that
    // late operations are rejected immediately while dispose waits for the lock.
    private int _disposed;

    /// <summary>Current lifecycle state. Lock-free read.</summary>
    public LifecycleState State => _state;

    /// <summary>True once <see cref="ConfigureAsync"/> has succeeded at least once and the provider has not been disposed.</summary>
    public bool IsConfigured => _config is not null;

    /// <summary>True when the FSM believes the provider is in <see cref="LifecycleState.Connected"/>. Lock-free.</summary>
    public bool IsConnectedSnapshot => _state == LifecycleState.Connected;

    /// <summary>The current configuration snapshot (lock-free volatile read), or null if never configured / configuration was never published.</summary>
    public DiscordProviderConfig? ConfigSnapshot => _config;

    /// <summary>For tests / diagnostics: number of operations currently in-flight via the drain tracker.</summary>
    internal long InFlightCount => _drainTracker.InFlightCount;

    /// <summary>For tests / diagnostics: whether the drain tracker considers teardown in progress.</summary>
    internal bool IsTeardownInProgress => _drainTracker.IsTeardownInProgress;

    /// <summary>
    /// Test-only seam: forces the FSM into <paramref name="state"/> without
    /// running the underlying connect/disconnect protocol. Lets concurrency
    /// tests assert the drain-on-disconnect contract without standing up a
    /// real <see cref="Discord.WebSocket.DiscordSocketClient"/>. Production
    /// code MUST drive transitions through the lease APIs.
    /// </summary>
    internal void ForceStateForTesting(LifecycleState state, DiscordProviderConfig? config = null)
    {
        _state = state;
        if (config is not null) _config = config;
    }

    /// <summary>
    /// Applies a parsed configuration. Owns OwnerId-preservation: when
    /// <paramref name="parsed"/> has no OwnerId but a previous configuration
    /// did, the previous OwnerId is preserved. Returns the effective config.
    /// </summary>
    /// <exception cref="ObjectDisposedException">If the provider has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// If the current state is <see cref="LifecycleState.Connected"/> or
    /// <see cref="LifecycleState.Connecting"/> (decision A1: callers must
    /// disconnect before reconfiguring a live connection).
    /// </exception>
    public async Task<DiscordProviderConfig> ConfigureAsync(DiscordProviderConfig parsed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ThrowIfDisposed();

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            switch (_state)
            {
                case LifecycleState.Created:
                case LifecycleState.Configured:
                case LifecycleState.Disconnecting:
                    // Disconnecting is reachable here only if a Disconnect raced and
                    // released the lock between BeginDisconnect and CompleteDisconnect.
                    // In practice CompleteDisconnect runs under the same lock, so by
                    // the time we observe Disconnecting we're between BeginDisconnect's
                    // teardown and CompleteDisconnect's state flip — but BeginDisconnect
                    // already holds the lock so we never observe this state under the
                    // lock from a queued caller. Defensive: treat as Configured-equivalent.
                    break;

                case LifecycleState.Connecting:
                case LifecycleState.Connected:
                    throw new InvalidOperationException(
                        $"Cannot reconfigure Discord provider in state {_state}. Call DisconnectAsync first.");

                case LifecycleState.Disposing:
                case LifecycleState.Disposed:
                    // Should not be reachable: ThrowIfDisposed above handles this.
                    throw new ObjectDisposedException(nameof(DiscordNotificationProvider));

                default:
                    throw new InvalidOperationException($"Unknown lifecycle state: {_state}");
            }

            // OwnerId preservation: if the new config omits OwnerId but the prior
            // config had one, keep the prior value. Silently widening access scope
            // on reconfigure would be a surprising regression.
            var effective = parsed;
            if (effective.OwnerId is null && _config?.OwnerId is { } previousOwnerId)
                effective = effective with { OwnerId = previousOwnerId };

            _config = effective;
            _state = LifecycleState.Configured;
            return effective;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Begins a connection attempt. Acquires <see cref="_connectLock"/> and
    /// transitions to <see cref="LifecycleState.Connecting"/>. Returns a lease
    /// that holds the lock until disposed.
    ///
    /// <para>
    /// The caller must invoke <see cref="ConnectLease.Complete"/> ONLY after
    /// every post-connect initialization step (e.g. router hook, channel
    /// rebuild) has succeeded. If the lease is disposed without
    /// <see cref="ConnectLease.Complete"/> being called, the FSM rolls back to
    /// <see cref="LifecycleState.Configured"/> automatically. This guarantees
    /// that an exception escaping the connect path cannot leave state stuck in
    /// <c>Connecting</c>.
    /// </para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">If the provider has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// If the current state does not permit a connect (e.g. no config yet, or
    /// already <c>Connected</c>).
    /// </exception>
    public async Task<ConnectLease> BeginConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _connectLock.WaitAsync(cancellationToken);

        // From here, any throw must release the lock.
        try
        {
            ThrowIfDisposed();

            switch (_state)
            {
                case LifecycleState.Configured:
                    // Legal transition: Configured --Connect--> Connecting.
                    break;

                case LifecycleState.Connected:
                    // Idempotent: caller treats this as "already connected, no-op".
                    return ConnectLease.AlreadyConnected(this, _config!);

                case LifecycleState.Created:
                    throw new InvalidOperationException(
                        "Discord provider must be configured before connecting. Call ConfigureAsync first.");

                case LifecycleState.Connecting:
                    // Should be unreachable: Connecting is held under the lock we just acquired.
                    throw new InvalidOperationException(
                        "Internal error: observed Connecting after acquiring connect lock.");

                case LifecycleState.Disconnecting:
                case LifecycleState.Disposing:
                case LifecycleState.Disposed:
                    throw new InvalidOperationException(
                        $"Cannot connect Discord provider while in state {_state}.");

                default:
                    throw new InvalidOperationException($"Unknown lifecycle state: {_state}");
            }

            var config = _config!; // Configured guarantees non-null.
            _state = LifecycleState.Connecting;
            return ConnectLease.New(this, config);
        }
        catch
        {
            _connectLock.Release();
            throw;
        }
    }

    /// <summary>
    /// Begins a disconnect. Acquires <see cref="_connectLock"/>. Returns a
    /// lease whose <see cref="DisconnectLease.NeedsTeardown"/> tells the
    /// caller whether to actually drain + dispose the underlying client
    /// (true only when transitioning from <see cref="LifecycleState.Connected"/>).
    ///
    /// <para>
    /// In <see cref="LifecycleState.Created"/>, <see cref="LifecycleState.Configured"/>,
    /// or <see cref="LifecycleState.Disconnecting"/>, the lease is granted
    /// idempotently with <c>NeedsTeardown == false</c> — no work to do
    /// (decision C: idempotent return).
    /// </para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">If the provider has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// If the current state is <see cref="LifecycleState.Connecting"/> (which
    /// shouldn't be reachable from outside the lock).
    /// </exception>
    public async Task<DisconnectLease> BeginDisconnectAsync(CancellationToken cancellationToken)
    {
        // Decision C: Disconnect from Disposed/Disposing is an idempotent no-op,
        // not a throw. We check BEFORE acquiring the lock because Dispose sets
        // _disposed = 1 prior to lock acquisition, so a Disconnect that arrives
        // after Dispose has begun must not contend for the lock at all.
        if (Volatile.Read(ref _disposed) == 1)
            return DisconnectLease.NoOpNoLock();

        await _connectLock.WaitAsync(cancellationToken);

        try
        {
            // Re-check under the lock: Dispose may have started while we waited.
            if (Volatile.Read(ref _disposed) == 1)
            {
                _connectLock.Release();
                return DisconnectLease.NoOpNoLock();
            }

            switch (_state)
            {
                case LifecycleState.Created:
                case LifecycleState.Configured:
                case LifecycleState.Disconnecting:
                    // No live connection — idempotent no-op. Lease will release lock on Dispose.
                    return DisconnectLease.NoOp(this);

                case LifecycleState.Connected:
                    // Mark teardown intent BEFORE doing anything so concurrent
                    // operations are rejected promptly.
                    _drainTracker.BeginTeardown();
                    _state = LifecycleState.Disconnecting;
                    return DisconnectLease.NeedsTeardownLease(this, _drainTracker);

                case LifecycleState.Connecting:
                    // Should be unreachable: Connecting is held under the lock we just acquired.
                    throw new InvalidOperationException(
                        "Internal error: observed Connecting after acquiring connect lock.");

                case LifecycleState.Disposing:
                case LifecycleState.Disposed:
                    // Unreachable: _disposed is checked above. If we ever fall here,
                    // treat as no-op too (Decision C).
                    return DisconnectLease.NoOp(this);

                default:
                    throw new InvalidOperationException($"Unknown lifecycle state: {_state}");
            }
        }
        catch
        {
            _connectLock.Release();
            throw;
        }
    }

    /// <summary>
    /// Begins terminal disposal. Single-winner: only the first caller observes
    /// <c>true</c>; concurrent callers get <c>false</c> and a no-op lease.
    ///
    /// <para>
    /// Sets <c>_disposed = 1</c> and <see cref="OperationDrainTracker.BeginTeardown"/>
    /// IMMEDIATELY (before waiting on <see cref="_connectLock"/>) so that any
    /// late <see cref="TryEnterOperation"/> is rejected at once even while
    /// dispose is queued behind a long-running connect.
    /// </para>
    /// </summary>
    public async Task<DisposeLease> BeginDisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return DisposeLease.AlreadyDisposed();

        // Reject any new outbound operations immediately. _disposed alone isn't
        // enough because tracked ops check _drainTracker.
        _drainTracker.BeginTeardown();

        // Serialize with any in-flight ConnectAsync/DisconnectAsync. Use
        // CancellationToken.None — dispose must not be cancellable.
        await _connectLock.WaitAsync(CancellationToken.None);
        _state = LifecycleState.Disposing;
        return DisposeLease.NeedsTeardownLease(this, _drainTracker);
    }

    /// <summary>
    /// Tries to take a permission lease for an operation. Returns a lease
    /// whose <see cref="OperationLease.Permitted"/> indicates whether the
    /// operation may proceed. The lease MUST be disposed in a <c>finally</c>
    /// (or via <c>using</c>); permitted leases also decrement the drain counter
    /// on <c>Dispose</c>, rejected leases are no-ops.
    /// </summary>
    public OperationLease TryEnterOperation(OperationKind kind)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return OperationLease.Rejected("provider is disposed");

        // Snapshot state once for consistency across the kind check.
        var state = _state;
        var requiresConnected = kind is OperationKind.Send or OperationKind.RequestInput;

        if (requiresConnected)
        {
            if (state != LifecycleState.Connected)
                return OperationLease.Rejected($"operation {kind} requires Connected, was {state}");
        }
        else
        {
            // RoomLifecycle: best-effort. Allowed in Configured / Connecting / Connected.
            // In Created we have nothing to act on; in Disconnecting/Disposing/Disposed
            // we're tearing down and shouldn't start new work.
            if (state is not (LifecycleState.Configured or LifecycleState.Connecting or LifecycleState.Connected))
                return OperationLease.Rejected($"operation {kind} not permitted in state {state}");
        }

        if (!_drainTracker.TryEnter())
            return OperationLease.Rejected("teardown in progress");

        // Re-check disposed after entering: dispose may have set _disposed
        // between our first read and the tracker increment.
        if (Volatile.Read(ref _disposed) == 1)
        {
            _drainTracker.Leave();
            return OperationLease.Rejected("provider is disposed");
        }

        return OperationLease.Granted(_drainTracker);
    }

    /// <summary>
    /// Test-only seam: takes a drain-tracked lease WITHOUT enforcing the
    /// state-based gate. Lets concurrency tests simulate an in-flight outbound
    /// op without needing a live Discord client. Production callers MUST go
    /// through <see cref="TryEnterOperation"/>.
    /// </summary>
    internal OperationLease TryEnterDrainOperationForTesting()
    {
        if (Volatile.Read(ref _disposed) == 1)
            return OperationLease.Rejected("provider is disposed");
        if (!_drainTracker.TryEnter())
            return OperationLease.Rejected("teardown in progress");
        if (Volatile.Read(ref _disposed) == 1)
        {
            _drainTracker.Leave();
            return OperationLease.Rejected("provider is disposed");
        }
        return OperationLease.Granted(_drainTracker);
    }

    /// <summary>
    /// Waits for in-flight operations to drain, bounded by <paramref name="timeout"/>.
    /// Used by Disconnect / Dispose paths under the connect lock. Does NOT throw on
    /// timeout — callers can detect via <see cref="InFlightCount"/> or rely on
    /// teardown proceeding regardless.
    /// </summary>
    public async Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _drainTracker.WaitForDrainAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(DiscordNotificationProvider));
    }

    // ===== Lease completion callbacks (called by lease Dispose) =====

    internal void FinishConnect(bool completed)
    {
        try
        {
            // Defensive: if dispose ran while we were holding the lock for connect,
            // _state may already be Disposing. Don't overwrite terminal intent.
            if (_state == LifecycleState.Disposing || _state == LifecycleState.Disposed)
            {
                Debug.Assert(Volatile.Read(ref _disposed) == 1);
                return;
            }

            if (completed)
            {
                // Connecting -> Connected. AlreadyConnected leases also pass completed=true
                // but in that case _state is already Connected and the assignment is a no-op.
                _state = LifecycleState.Connected;
            }
            else
            {
                // Rollback path: Connecting -> Configured.
                if (_state == LifecycleState.Connecting)
                    _state = LifecycleState.Configured;
                // If state isn't Connecting (e.g. AlreadyConnected lease disposed
                // without Complete), don't downgrade Connected to Configured.
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    internal void FinishDisconnect(bool needsTeardown)
    {
        try
        {
            if (_state == LifecycleState.Disposing || _state == LifecycleState.Disposed)
            {
                Debug.Assert(Volatile.Read(ref _disposed) == 1);
                return;
            }

            if (needsTeardown)
            {
                // Disconnecting -> Configured (recoverable). Always EndTeardown so
                // future ops can enter again.
                _state = LifecycleState.Configured;
                _drainTracker.EndTeardown();
            }
            // Else: no-op disconnect; state and teardown flag are unchanged.
        }
        finally
        {
            _connectLock.Release();
        }
    }

    internal void FinishDispose()
    {
        try
        {
            // Disposing -> Disposed. NEVER call EndTeardown — terminal.
            _state = LifecycleState.Disposed;
        }
        finally
        {
            _connectLock.Release();
            // Intentionally NOT disposing _connectLock — see DiscordNotificationProvider
            // history (a late ConnectAsync racing into WaitAsync after Dispose was
            // exactly the bug we're avoiding).
        }
    }
}

/// <summary>
/// Permission lease returned by <see cref="DiscordProviderLifecycle.TryEnterOperation"/>.
/// Reference type with idempotent <see cref="Dispose"/>: a copy or repeated
/// disposal will not over-release the underlying drain counter.
/// </summary>
internal sealed class OperationLease : IDisposable
{
    private OperationDrainTracker? _tracker;

    public bool Permitted { get; }
    public string? RejectionReason { get; }

    private OperationLease(bool permitted, string? rejectionReason, OperationDrainTracker? tracker)
    {
        Permitted = permitted;
        RejectionReason = rejectionReason;
        _tracker = tracker;
    }

    public static OperationLease Rejected(string reason) => new(false, reason, null);
    public static OperationLease Granted(OperationDrainTracker tracker) => new(true, null, tracker);

    public void Dispose()
    {
        // Atomic exchange ensures Dispose is idempotent even under concurrent calls.
        var t = Interlocked.Exchange(ref _tracker, null);
        t?.Leave();
    }
}

/// <summary>
/// Connect lease. Holds the lifecycle lock. Caller must call
/// <see cref="Complete"/> after every post-connect initialization step has
/// succeeded; otherwise <see cref="DisposeAsync"/> rolls the FSM back to
/// <see cref="LifecycleState.Configured"/> (with the lock released).
/// </summary>
internal sealed class ConnectLease : IAsyncDisposable
{
    private DiscordProviderLifecycle? _owner;
    private bool _completed;

    /// <summary>The configuration captured at lock-acquisition time. Use this for the connect call rather than re-reading from the provider — guarantees no mid-flight swap.</summary>
    public DiscordProviderConfig Config { get; }

    /// <summary>True if the FSM was already <see cref="LifecycleState.Connected"/> when the lease was issued. The caller should treat this as "no work to do".</summary>
    public bool AlreadyConnectedFlag { get; }

    private ConnectLease(DiscordProviderLifecycle owner, DiscordProviderConfig config, bool alreadyConnected, bool autoComplete)
    {
        _owner = owner;
        Config = config;
        AlreadyConnectedFlag = alreadyConnected;
        if (autoComplete) _completed = true;
    }

    internal static ConnectLease New(DiscordProviderLifecycle owner, DiscordProviderConfig config)
        => new(owner, config, alreadyConnected: false, autoComplete: false);

    internal static ConnectLease AlreadyConnected(DiscordProviderLifecycle owner, DiscordProviderConfig config)
        => new(owner, config, alreadyConnected: true, autoComplete: true);

    /// <summary>Marks the connect successful. The FSM will transition to <see cref="LifecycleState.Connected"/> when this lease is disposed.</summary>
    public void Complete() => _completed = true;

    public ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null) return ValueTask.CompletedTask;
        owner.FinishConnect(_completed);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Disconnect lease. Holds the lifecycle lock. <see cref="NeedsTeardown"/> is
/// <c>true</c> only when the FSM was actually in <see cref="LifecycleState.Connected"/>.
/// On <see cref="DisposeAsync"/>, the FSM returns to <see cref="LifecycleState.Configured"/>
/// and clears the teardown flag (recoverable path).
/// </summary>
internal sealed class DisconnectLease : IAsyncDisposable
{
    private DiscordProviderLifecycle? _owner;
    private readonly bool _needsTeardown;

    public bool NeedsTeardown => _needsTeardown;

    /// <summary>The drain tracker — exposed so the provider can call <see cref="OperationDrainTracker.WaitForDrainAsync"/> under the held lock. <c>null</c> when <see cref="NeedsTeardown"/> is false.</summary>
    public OperationDrainTracker? DrainTracker { get; }

    private DisconnectLease(DiscordProviderLifecycle? owner, bool needsTeardown, OperationDrainTracker? tracker)
    {
        _owner = owner;
        _needsTeardown = needsTeardown;
        DrainTracker = tracker;
    }

    internal static DisconnectLease NoOp(DiscordProviderLifecycle owner)
        => new(owner, needsTeardown: false, tracker: null);

    /// <summary>
    /// No-op lease that does NOT hold the lifecycle lock. Returned when
    /// Disconnect is called after Dispose has begun (Decision C). DisposeAsync
    /// is a true no-op — owner is null so no FinishDisconnect callback fires
    /// and no lock release is attempted.
    /// </summary>
    internal static DisconnectLease NoOpNoLock()
        => new(owner: null, needsTeardown: false, tracker: null);

    internal static DisconnectLease NeedsTeardownLease(DiscordProviderLifecycle owner, OperationDrainTracker tracker)
        => new(owner, needsTeardown: true, tracker: tracker);

    public ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null) return ValueTask.CompletedTask;
        owner.FinishDisconnect(_needsTeardown);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Dispose lease. Terminal — on <see cref="DisposeAsync"/>, FSM goes to
/// <see cref="LifecycleState.Disposed"/> and does NOT call
/// <see cref="OperationDrainTracker.EndTeardown"/>.
/// <see cref="ShouldRunTeardown"/> is <c>false</c> for the second concurrent
/// caller (single-winner gate) — they should return immediately.
/// </summary>
internal sealed class DisposeLease : IAsyncDisposable
{
    private DiscordProviderLifecycle? _owner;
    private readonly bool _shouldRunTeardown;

    public bool ShouldRunTeardown => _shouldRunTeardown;
    public OperationDrainTracker? DrainTracker { get; }

    private DisposeLease(DiscordProviderLifecycle? owner, bool shouldRunTeardown, OperationDrainTracker? tracker)
    {
        _owner = owner;
        _shouldRunTeardown = shouldRunTeardown;
        DrainTracker = tracker;
    }

    internal static DisposeLease AlreadyDisposed()
        => new(owner: null, shouldRunTeardown: false, tracker: null);

    internal static DisposeLease NeedsTeardownLease(DiscordProviderLifecycle owner, OperationDrainTracker tracker)
        => new(owner, shouldRunTeardown: true, tracker: tracker);

    public ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null) return ValueTask.CompletedTask;
        owner.FinishDispose();
        return ValueTask.CompletedTask;
    }
}
