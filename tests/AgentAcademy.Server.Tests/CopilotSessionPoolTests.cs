using System.Reflection;
using AgentAcademy.Server.Services;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class CopilotSessionPoolTests : IAsyncDisposable
{
    private readonly CopilotSessionPool _pool;
    private int _factoryCallCount;

    public CopilotSessionPoolTests()
    {
        _pool = new CopilotSessionPool(NullLogger<CopilotSessionPool>.Instance);
    }

    public async ValueTask DisposeAsync() => await _pool.DisposeAsync();

    /// <summary>
    /// Creates a CopilotSession via reflection. The SDK type is sealed with
    /// an internal constructor, so this is the only way to create test instances.
    /// DisposeAsync will throw (null JsonRpc) but the pool catches that.
    /// </summary>
    private static CopilotSession CreateFakeSession(string sessionId = "test")
    {
        // Select the 4-parameter internal constructor (sessionId, rpc, logger, workspacePath)
        // by matching parameter count and name to avoid breakage if SDK adds overloads.
        var ctor = typeof(CopilotSession)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == 4 && ps[0].Name == "sessionId";
            })
            ?? throw new InvalidOperationException(
                "CopilotSession internal constructor not found — SDK may have changed.");
        return (CopilotSession)ctor.Invoke([sessionId, null, null, null]);
    }

    private Task<CopilotSession> CountingFactory(CancellationToken ct)
    {
        Interlocked.Increment(ref _factoryCallCount);
        return Task.FromResult(CreateFakeSession());
    }

    // ── UseAsync: session creation and caching ─────────────────────

    [Fact]
    public async Task UseAsync_CallsFactory_OnFirstAccess()
    {
        var result = await _pool.UseAsync(
            "key1", CountingFactory,
            _ => Task.FromResult(42),
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, _factoryCallCount);
    }

    [Fact]
    public async Task UseAsync_ReusesSession_ForSameKey()
    {
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);

        Assert.Equal(1, _factoryCallCount);
    }

    [Fact]
    public async Task UseAsync_CreatesSeparateSessions_ForDifferentKeys()
    {
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("key2", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("key3", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);

        Assert.Equal(3, _factoryCallCount);
    }

    [Fact]
    public async Task UseAsync_PassesSessionToAction()
    {
        CopilotSession? captured = null;

        await _pool.UseAsync(
            "key1", CountingFactory,
            session =>
            {
                captured = session;
                return Task.FromResult(0);
            },
            CancellationToken.None);

        Assert.NotNull(captured);
    }

    [Fact]
    public async Task UseAsync_ReturnsDifferentSessions_ForDifferentKeys()
    {
        CopilotSession? session1 = null;
        CopilotSession? session2 = null;

        await _pool.UseAsync("key1", CountingFactory,
            s => { session1 = s; return Task.FromResult(0); },
            CancellationToken.None);
        await _pool.UseAsync("key2", CountingFactory,
            s => { session2 = s; return Task.FromResult(0); },
            CancellationToken.None);

        Assert.NotNull(session1);
        Assert.NotNull(session2);
        Assert.NotSame(session1, session2);
    }

    [Fact]
    public async Task UseAsync_ReturnsSameSession_ForSameKey()
    {
        CopilotSession? first = null;
        CopilotSession? second = null;

        await _pool.UseAsync("key1", CountingFactory,
            s => { first = s; return Task.FromResult(0); },
            CancellationToken.None);
        await _pool.UseAsync("key1", CountingFactory,
            s => { second = s; return Task.FromResult(0); },
            CancellationToken.None);

        Assert.Same(first, second);
    }

    // ── UseAsync: lock behavior ────────────────────────────────────

    [Fact]
    public async Task UseAsync_SerializesAccess_ForSameKey()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;

        var tasks = Enumerable.Range(0, 5).Select(_ =>
            _pool.UseAsync("key1", CountingFactory, async session =>
            {
                var current = Interlocked.Increment(ref concurrentCount);
                // Track max concurrent access
                int prev;
                do { prev = maxConcurrent; }
                while (current > prev &&
                       Interlocked.CompareExchange(ref maxConcurrent, current, prev) != prev);

                await Task.Delay(50);
                Interlocked.Decrement(ref concurrentCount);
                return 0;
            }, CancellationToken.None));

        await Task.WhenAll(tasks);

        // Per-key send lock means max concurrency of 1
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task UseAsync_AllowsConcurrentAccess_AcrossKeys()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var barrier = new TaskCompletionSource();

        var tasks = Enumerable.Range(0, 3).Select(i =>
            _pool.UseAsync($"key{i}", CountingFactory, async session =>
            {
                var current = Interlocked.Increment(ref concurrentCount);
                int prev;
                do { prev = maxConcurrent; }
                while (current > prev &&
                       Interlocked.CompareExchange(ref maxConcurrent, current, prev) != prev);

                // Wait for all tasks to be running concurrently
                if (Volatile.Read(ref concurrentCount) >= 3)
                    barrier.TrySetResult();
                else
                    await Task.WhenAny(barrier.Task, Task.Delay(2000));

                Interlocked.Decrement(ref concurrentCount);
                return 0;
            }, CancellationToken.None));

        await Task.WhenAll(tasks);

        // Different keys can run concurrently
        Assert.True(maxConcurrent >= 2, $"Expected concurrency >= 2 but got {maxConcurrent}");
    }

    [Fact]
    public async Task UseAsync_ReleasesLock_OnActionException()
    {
        // First call throws
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _pool.UseAsync<int>("key1", CountingFactory,
                _ => throw new InvalidOperationException("boom"),
                CancellationToken.None));

        // Second call should not deadlock — lock was released
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await _pool.UseAsync("key1", CountingFactory,
            _ => Task.FromResult(99), cts.Token);

        Assert.Equal(99, result);
    }

    [Fact]
    public async Task UseAsync_PropagatesActionException()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _pool.UseAsync<int>("key1", CountingFactory,
                _ => throw new ArgumentException("bad arg"),
                CancellationToken.None));

        Assert.Equal("bad arg", ex.Message);
    }

    [Fact]
    public async Task UseAsync_PropagatesFactoryException()
    {
        Task<CopilotSession> FailingFactory(CancellationToken ct) =>
            throw new TimeoutException("factory timeout");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            _pool.UseAsync("key1", FailingFactory,
                _ => Task.FromResult(0), CancellationToken.None));
    }

    [Fact]
    public async Task UseAsync_HonoursCancellationToken()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // The per-key lock WaitAsync should throw on a cancelled token
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _pool.UseAsync("key1", CountingFactory,
                _ => Task.FromResult(0), cts.Token));
    }

    // ── Invalidation ───────────────────────────────────────────────

    [Fact]
    public async Task InvalidateAsync_CausesNewFactoryCall()
    {
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(1, _factoryCallCount);

        await _pool.InvalidateAsync("key1");

        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(2, _factoryCallCount);
    }

    [Fact]
    public async Task InvalidateAsync_NoOp_ForUnknownKey()
    {
        // Should not throw
        await _pool.InvalidateAsync("nonexistent");
    }

    [Fact]
    public async Task InvalidateAsync_ProducesNewSession()
    {
        CopilotSession? before = null;
        CopilotSession? after = null;

        await _pool.UseAsync("key1", CountingFactory,
            s => { before = s; return Task.FromResult(0); },
            CancellationToken.None);

        await _pool.InvalidateAsync("key1");

        await _pool.UseAsync("key1", CountingFactory,
            s => { after = s; return Task.FromResult(0); },
            CancellationToken.None);

        Assert.NotSame(before, after);
    }

    [Fact]
    public async Task InvalidateByFilterAsync_RemovesMatchingSessions()
    {
        await _pool.UseAsync("agent:alpha", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("agent:beta", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("other:gamma", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(3, _factoryCallCount);

        // Remove only "agent:" prefixed sessions
        await _pool.InvalidateByFilterAsync(key => key.StartsWith("agent:"));

        await _pool.UseAsync("agent:alpha", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("agent:beta", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("other:gamma", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);

        // alpha and beta recreated (2 new), gamma reused (0 new) = 5 total
        Assert.Equal(5, _factoryCallCount);
    }

    [Fact]
    public async Task InvalidateByFilterAsync_KeepsNonMatchingSessions()
    {
        CopilotSession? gammaFirst = null;
        CopilotSession? gammaSecond = null;

        await _pool.UseAsync("agent:alpha", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("other:gamma", CountingFactory,
            s => { gammaFirst = s; return Task.FromResult(0); },
            CancellationToken.None);

        await _pool.InvalidateByFilterAsync(key => key.StartsWith("agent:"));

        await _pool.UseAsync("other:gamma", CountingFactory,
            s => { gammaSecond = s; return Task.FromResult(0); },
            CancellationToken.None);

        Assert.Same(gammaFirst, gammaSecond);
    }

    [Fact]
    public async Task InvalidateByFilterAsync_NoOp_WhenNoneMatch()
    {
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);

        await _pool.InvalidateByFilterAsync(_ => false);

        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(1, _factoryCallCount);
    }

    [Fact]
    public async Task InvalidateAllAsync_CausesAllNewFactoryCalls()
    {
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("key2", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(2, _factoryCallCount);

        await _pool.InvalidateAllAsync();

        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await _pool.UseAsync("key2", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(4, _factoryCallCount);
    }

    // ── Dispose ────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var pool = new CopilotSessionPool(NullLogger<CopilotSessionPool>.Instance);
        await pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);

        // Double dispose should not throw
        await pool.DisposeAsync();
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CleansUpSessions()
    {
        var pool = new CopilotSessionPool(NullLogger<CopilotSessionPool>.Instance);
        await pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        await pool.UseAsync("key2", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);

        // Dispose triggers DisposeSessionSafeAsync for each entry
        // (our fake sessions throw on dispose, but pool catches it)
        await pool.DisposeAsync();
    }

    // ── Edge cases ─────────────────────────────────────────────────

    [Fact]
    public async Task UseAsync_WorksWithEmptyStringKey()
    {
        var result = await _pool.UseAsync(
            "", CountingFactory,
            _ => Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task UseAsync_ConcurrentCreation_SameKey_OnlyOneFactoryCall()
    {
        // Use a barrier to ensure multiple callers overlap at the factory
        var enteredFactory = 0;
        var gate = new SemaphoreSlim(0, 1);

        async Task<CopilotSession> GatedFactory(CancellationToken ct)
        {
            var count = Interlocked.Increment(ref enteredFactory);
            if (count == 1)
            {
                // First caller: pause inside the factory so others queue up
                await gate.WaitAsync(ct);
            }
            return CreateFakeSession();
        }

        // Launch concurrent requests for the same key
        var tasks = Enumerable.Range(0, 10).Select(_ =>
            _pool.UseAsync("race-key", GatedFactory,
                _ => Task.FromResult(0),
                CancellationToken.None)).ToArray();

        // Give time for all tasks to reach the per-key creation lock
        await Task.Delay(100);

        // Release the factory gate so the first (winning) caller completes
        gate.Release();

        await Task.WhenAll(tasks);

        // Double-check locking ensures only 1 factory call
        Assert.Equal(1, enteredFactory);
    }

    [Fact]
    public async Task InvalidateAsync_WhileUseAsyncHoldsLock_CompletesWithoutDeadlock()
    {
        // Pre-create a session
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(1, _factoryCallCount);

        // Hold the send lock open while invalidation runs concurrently
        var actionStarted = new TaskCompletionSource();
        var allowFinish = new TaskCompletionSource();

        var useTask = _pool.UseAsync("key1", CountingFactory, async _ =>
        {
            actionStarted.SetResult();
            await allowFinish.Task;
            return 0;
        }, CancellationToken.None);

        // Wait for the action to be running (holds send lock)
        await actionStarted.Task;

        // InvalidateAsync tries to TryRemove from the dictionary —
        // it should complete even while the send lock is held.
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var invalidateTask = _pool.InvalidateAsync("key1");
        var completed = await Task.WhenAny(invalidateTask, Task.Delay(Timeout.Infinite, cts.Token));
        Assert.Same(invalidateTask, completed); // Invalidate didn't deadlock

        // Let the in-flight action finish
        allowFinish.SetResult();
        await useTask;

        // Next call should create a new session
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(2, _factoryCallCount);
    }

    [Fact]
    public async Task UseAsync_AfterInvalidateAll_RecreatesAll()
    {
        // Create 5 sessions
        for (int i = 0; i < 5; i++)
            await _pool.UseAsync($"k{i}", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(5, _factoryCallCount);

        await _pool.InvalidateAllAsync();

        // All should be recreated
        for (int i = 0; i < 5; i++)
            await _pool.UseAsync($"k{i}", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(10, _factoryCallCount);
    }

    [Fact]
    public async Task UseAsync_GenericType_WorksWithDifferentTypes()
    {
        var intResult = await _pool.UseAsync(
            "key1", CountingFactory,
            _ => Task.FromResult(42),
            CancellationToken.None);
        Assert.Equal(42, intResult);

        var stringResult = await _pool.UseAsync(
            "key2", CountingFactory,
            _ => Task.FromResult("hello"),
            CancellationToken.None);
        Assert.Equal("hello", stringResult);
    }

    // ── TTL expiry ─────────────────────────────────────────────────

    /// <summary>
    /// Helper: accesses the private _sessions dictionary and sets _lastUsed
    /// on the SessionEntry for the given key to simulate TTL expiry.
    /// </summary>
    private void ForceEntryExpiry(string key, TimeSpan age)
    {
        var sessionsField = typeof(CopilotSessionPool)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = sessionsField.GetValue(_pool)!;

        // ConcurrentDictionary<string, SessionEntry>.TryGetValue via reflection
        var tryGetMethod = dict.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { key, null };
        var found = (bool)tryGetMethod.Invoke(dict, args)!;
        Assert.True(found, $"Key '{key}' not found in pool");

        var entry = args[1]!;
        var lastUsedField = entry.GetType()
            .GetField("_lastUsed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        lastUsedField.SetValue(entry, DateTime.UtcNow - age);
    }

    [Fact]
    public async Task UseAsync_RecreatesSession_WhenExpired()
    {
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(1, _factoryCallCount);

        // Force the entry to be 15 minutes old (TTL is 10 minutes)
        ForceEntryExpiry("key1", TimeSpan.FromMinutes(15));

        // Next UseAsync should detect expiry and call the factory again
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);
        Assert.Equal(2, _factoryCallCount);
    }

    [Fact]
    public async Task UseAsync_RefreshesTtl_OnSuccessfulUse()
    {
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);

        // Set close to expiry (9 minutes, TTL is 10)
        ForceEntryExpiry("key1", TimeSpan.FromMinutes(9));

        // Use the session — should refresh TTL via Touch()
        await _pool.UseAsync("key1", CountingFactory, _ => Task.FromResult(0), CancellationToken.None);

        // Read _lastUsed via reflection to verify refresh
        var sessionsField = typeof(CopilotSessionPool)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = sessionsField.GetValue(_pool)!;
        var tryGetMethod = dict.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { "key1", null };
        tryGetMethod.Invoke(dict, args);
        var entry = args[1]!;
        var lastUsedField = entry.GetType()
            .GetField("_lastUsed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var lastUsed = (DateTime)lastUsedField.GetValue(entry)!;

        Assert.True(DateTime.UtcNow - lastUsed < TimeSpan.FromSeconds(5),
            $"Expected TTL refresh but _lastUsed is {DateTime.UtcNow - lastUsed} ago");

        // Only one factory call — session was reused, not recreated
        Assert.Equal(1, _factoryCallCount);
    }

    [Fact]
    public async Task UseAsync_DisposesExpiredSession_BeforeCreatingNew()
    {
        CopilotSession? firstSession = null;
        CopilotSession? secondSession = null;

        await _pool.UseAsync("key1", CountingFactory,
            s => { firstSession = s; return Task.FromResult(0); },
            CancellationToken.None);

        ForceEntryExpiry("key1", TimeSpan.FromMinutes(15));

        await _pool.UseAsync("key1", CountingFactory,
            s => { secondSession = s; return Task.FromResult(0); },
            CancellationToken.None);

        Assert.NotSame(firstSession, secondSession);
    }
}
