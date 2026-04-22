using AgentAcademy.Server.Notifications;

namespace AgentAcademy.Server.Tests;

public class OperationDrainTrackerTests
{
    [Fact]
    public void TryEnter_WhenNotInTeardown_ReturnsTrue()
    {
        var tracker = new OperationDrainTracker();

        Assert.True(tracker.TryEnter());
        Assert.Equal(1, tracker.InFlightCount);

        tracker.Leave();
    }

    [Fact]
    public void TryEnter_DuringTeardown_ReturnsFalse()
    {
        var tracker = new OperationDrainTracker();
        tracker.BeginTeardown();

        Assert.False(tracker.TryEnter());
        Assert.Equal(0, tracker.InFlightCount);
    }

    [Fact]
    public void TryEnter_AfterEndTeardown_ReturnsTrue()
    {
        var tracker = new OperationDrainTracker();
        tracker.BeginTeardown();
        tracker.EndTeardown();

        Assert.True(tracker.TryEnter());
        Assert.Equal(1, tracker.InFlightCount);

        tracker.Leave();
    }

    [Fact]
    public void Leave_DecrementsCount()
    {
        var tracker = new OperationDrainTracker();
        tracker.TryEnter();
        tracker.TryEnter();
        Assert.Equal(2, tracker.InFlightCount);

        tracker.Leave();
        Assert.Equal(1, tracker.InFlightCount);

        tracker.Leave();
        Assert.Equal(0, tracker.InFlightCount);
    }

    [Fact]
    public async Task WaitForDrainAsync_WhenNoOperations_ReturnsImmediately()
    {
        var tracker = new OperationDrainTracker();

        // Should not throw or hang
        await tracker.WaitForDrainAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
    }

    [Fact]
    public async Task WaitForDrainAsync_CompletesWhenLastOperationLeaves()
    {
        var tracker = new OperationDrainTracker();
        tracker.TryEnter();

        var drainTask = tracker.WaitForDrainAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(drainTask.IsCompleted);

        tracker.Leave();
        await drainTask; // Should complete now
    }

    [Fact]
    public async Task WaitForDrainAsync_TimesOut_WhenOperationsStillInFlight()
    {
        var tracker = new OperationDrainTracker();
        tracker.TryEnter();

        await Assert.ThrowsAsync<TimeoutException>(
            () => tracker.WaitForDrainAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        // Cleanup
        tracker.Leave();
    }

    [Fact]
    public async Task WaitForDrainAsync_RespectsCancel()
    {
        var tracker = new OperationDrainTracker();
        tracker.TryEnter();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tracker.WaitForDrainAsync(TimeSpan.FromSeconds(30), cts.Token));

        tracker.Leave();
    }

    [Fact]
    public async Task WaitForDrainAsync_MultipleOperations_WaitsForAll()
    {
        var tracker = new OperationDrainTracker();
        tracker.TryEnter();
        tracker.TryEnter();
        tracker.TryEnter();

        var drainTask = tracker.WaitForDrainAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        tracker.Leave(); // 2 remaining
        Assert.False(drainTask.IsCompleted);

        tracker.Leave(); // 1 remaining
        Assert.False(drainTask.IsCompleted);

        tracker.Leave(); // 0 remaining
        await drainTask;
    }

    [Fact]
    public void IsTeardownInProgress_ReflectsState()
    {
        var tracker = new OperationDrainTracker();
        Assert.False(tracker.IsTeardownInProgress);

        tracker.BeginTeardown();
        Assert.True(tracker.IsTeardownInProgress);

        tracker.EndTeardown();
        Assert.False(tracker.IsTeardownInProgress);
    }

    [Fact]
    public async Task ConcurrentEnterLeave_DoesNotCorruptState()
    {
        var tracker = new OperationDrainTracker();
        const int iterations = 10_000;
        var barrier = new Barrier(4);

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterations; i++)
            {
                if (tracker.TryEnter())
                {
                    Thread.SpinWait(10);
                    tracker.Leave();
                }
            }
        }));

        await Task.WhenAll(tasks);
        Assert.Equal(0, tracker.InFlightCount);
    }

    [Fact]
    public async Task ConcurrentEnterWithTeardown_DrainCompletes()
    {
        var tracker = new OperationDrainTracker();
        var enterCount = 0;
        var exitCount = 0;
        var barrier = new Barrier(3);

        // Two threads entering/leaving
        var opTasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < 1000; i++)
            {
                if (tracker.TryEnter())
                {
                    Interlocked.Increment(ref enterCount);
                    Thread.SpinWait(10);
                    tracker.Leave();
                    Interlocked.Increment(ref exitCount);
                }
            }
        }));

        // One thread doing teardown
        var teardownTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            await Task.Delay(5);
            tracker.BeginTeardown();
            await tracker.WaitForDrainAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        });

        await Task.WhenAll(opTasks.Append(teardownTask));

        Assert.Equal(0, tracker.InFlightCount);
        Assert.Equal(enterCount, exitCount);
    }

    [Fact]
    public async Task WaitForDrain_CalledMultipleTimes_AllComplete()
    {
        var tracker = new OperationDrainTracker();
        tracker.TryEnter();

        var drain1 = tracker.WaitForDrainAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var drain2 = tracker.WaitForDrainAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        tracker.Leave();

        // Both should complete
        await drain1;
        await drain2;
    }
}
