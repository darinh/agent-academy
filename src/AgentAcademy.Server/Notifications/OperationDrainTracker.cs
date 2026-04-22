using System.Diagnostics;

namespace AgentAcademy.Server.Notifications;

/// <summary>
/// Thread-safe tracker for in-flight outbound operations that need to drain
/// before a resource (e.g., a network client) can be safely torn down.
///
/// <para>
/// Callers use <see cref="TryEnter"/>/<see cref="Leave"/> to bracket each
/// operation and <see cref="BeginTeardown"/>/<see cref="WaitForDrainAsync"/>
/// to coordinate teardown. The tracker does NOT serialise teardown with
/// lifecycle operations — the caller is responsible for external locking
/// (e.g., a <see cref="SemaphoreSlim"/>) to prevent concurrent
/// connect/disconnect/dispose races.
/// </para>
///
/// <para>
/// Typical usage:
/// <code>
/// // Operation side:
/// if (!tracker.TryEnter()) return;
/// try { await DoWorkAsync(); }
/// finally { tracker.Leave(); }
///
/// // Teardown side (under external lock):
/// tracker.BeginTeardown();
/// try {
///     await tracker.WaitForDrainAsync(timeout, ct);
///     await DisposeResourceAsync();
/// } finally {
///     tracker.EndTeardown(); // only for recoverable teardown (e.g., Disconnect)
/// }
/// </code>
/// </para>
/// </summary>
internal sealed class OperationDrainTracker
{
    private long _inFlight;
    private volatile bool _teardownInProgress;
    private readonly object _drainLock = new();
    private TaskCompletionSource<bool>? _drainTcs;

    /// <summary>True when teardown has been initiated via <see cref="BeginTeardown"/>.</summary>
    public bool IsTeardownInProgress => _teardownInProgress;

    /// <summary>Number of operations currently in flight.</summary>
    public long InFlightCount => Interlocked.Read(ref _inFlight);

    /// <summary>
    /// Tries to enter an operation. Returns <see langword="true"/> if the
    /// operation can proceed; the caller MUST call <see cref="Leave"/> in a
    /// <c>finally</c> block when done. Returns <see langword="false"/> (and
    /// does NOT require <see cref="Leave"/>) when teardown is in progress.
    /// </summary>
    public bool TryEnter()
    {
        if (_teardownInProgress) return false;

        Interlocked.Increment(ref _inFlight);

        // Re-check after increment: teardown may have been initiated between
        // our first check and the increment. Back out to avoid keeping the
        // teardown waiter stuck.
        if (_teardownInProgress)
        {
            Leave();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Marks the end of an operation started by <see cref="TryEnter"/>.
    /// When the last operation completes, signals any pending drain waiter.
    /// </summary>
    public void Leave()
    {
        var remaining = Interlocked.Decrement(ref _inFlight);
        Debug.Assert(remaining >= 0, "OperationDrainTracker.Leave called without matching TryEnter");

        if (remaining != 0) return;

        // Last operation out — wake any teardown waiter.
        TaskCompletionSource<bool>? toComplete;
        lock (_drainLock)
        {
            toComplete = _drainTcs;
            _drainTcs = null;
        }
        toComplete?.TrySetResult(true);
    }

    /// <summary>
    /// Marks teardown as in progress. New <see cref="TryEnter"/> calls will
    /// return <see langword="false"/>. For recoverable teardown (e.g., Disconnect),
    /// call <see cref="EndTeardown"/> in a <c>finally</c> block. For terminal
    /// teardown (e.g., Dispose), omit <see cref="EndTeardown"/> — the object
    /// is dead and the flag stays set.
    /// </summary>
    public void BeginTeardown() => _teardownInProgress = true;

    /// <summary>
    /// Clears the teardown flag, allowing new operations to proceed.
    /// Only call this for recoverable teardown (e.g., Disconnect).
    /// </summary>
    public void EndTeardown() => _teardownInProgress = false;

    /// <summary>
    /// Waits for all in-flight operations to complete, bounded by
    /// <paramref name="timeout"/>. Returns immediately if no operations are
    /// in flight. On timeout, throws <see cref="TimeoutException"/> so the
    /// caller can log context-specific diagnostics.
    /// </summary>
    public async Task WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Interlocked.Read(ref _inFlight) == 0) return;

        TaskCompletionSource<bool> tcs;
        lock (_drainLock)
        {
            // Re-check inside the lock — Leave may have drained between our
            // first check and lock acquisition.
            if (Interlocked.Read(ref _inFlight) == 0) return;
            _drainTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _drainTcs;
        }

        await tcs.Task.WaitAsync(timeout, cancellationToken);
    }
}
