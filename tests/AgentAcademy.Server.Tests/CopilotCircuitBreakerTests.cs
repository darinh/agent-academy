using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

public class CopilotCircuitBreakerTests
{
    [Fact]
    public void InitialState_IsClosed()
    {
        var cb = new CopilotCircuitBreaker();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.ConsecutiveFailures);
        Assert.Null(cb.LastFailureUtc);
    }

    [Fact]
    public void AllowRequest_WhenClosed_ReturnsTrue()
    {
        var cb = new CopilotCircuitBreaker();
        Assert.True(cb.AllowRequest());
    }

    [Fact]
    public void RecordSuccess_WhenClosed_RemainsClosedAndZeroFailures()
    {
        var cb = new CopilotCircuitBreaker();
        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.ConsecutiveFailures);
    }

    [Fact]
    public void RecordFailure_BelowThreshold_RemainsClosed()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 5);

        for (int i = 0; i < 4; i++)
            cb.RecordFailure();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(4, cb.ConsecutiveFailures);
        Assert.True(cb.AllowRequest());
    }

    [Fact]
    public void RecordFailure_AtThreshold_OpensCircuit()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 3);

        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordFailure();

        Assert.Equal(CircuitState.Open, cb.State);
        Assert.Equal(3, cb.ConsecutiveFailures);
    }

    [Fact]
    public void AllowRequest_WhenOpen_ReturnsFalse()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 1);
        cb.RecordFailure();

        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.AllowRequest());
    }

    [Fact]
    public void Open_TransitionsToHalfOpen_AfterCooldown()
    {
        // Use a tiny cooldown for testing
        var cb = new CopilotCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMilliseconds(50));
        cb.RecordFailure();

        Assert.Equal(CircuitState.Open, cb.State);

        // Wait for cooldown
        Thread.Sleep(100);

        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    [Fact]
    public void HalfOpen_AllowsOneRequest()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMilliseconds(50));
        cb.RecordFailure();

        Thread.Sleep(100);
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // First request allowed (probe)
        Assert.True(cb.AllowRequest());

        // Second request blocked (circuit returned to Open during probe)
        Assert.False(cb.AllowRequest());
    }

    [Fact]
    public void HalfOpen_ProbeSuccess_ResetsToClosed()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMilliseconds(50));
        cb.RecordFailure();

        Thread.Sleep(100);
        Assert.True(cb.AllowRequest()); // probe allowed

        cb.RecordSuccess();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.ConsecutiveFailures);
        Assert.True(cb.AllowRequest());
    }

    [Fact]
    public void HalfOpen_ProbeFailure_ReopensCircuit()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMilliseconds(50));
        cb.RecordFailure();

        Thread.Sleep(100);
        Assert.True(cb.AllowRequest()); // probe allowed

        cb.RecordFailure();

        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.AllowRequest());
    }

    [Fact]
    public void RecordSuccess_ResetsConsecutiveFailures()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 5);

        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(3, cb.ConsecutiveFailures);

        cb.RecordSuccess();
        Assert.Equal(0, cb.ConsecutiveFailures);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public void SuccessAfterFailures_PreventsOpening()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 3);

        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordSuccess(); // resets
        cb.RecordFailure();
        cb.RecordFailure();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(2, cb.ConsecutiveFailures);
    }

    [Fact]
    public void Reset_RestoresClosedState()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 1);
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        cb.Reset();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.ConsecutiveFailures);
        Assert.True(cb.AllowRequest());
    }

    [Fact]
    public void LastFailureUtc_TracksLatestFailure()
    {
        var cb = new CopilotCircuitBreaker();
        Assert.Null(cb.LastFailureUtc);

        var before = DateTime.UtcNow;
        cb.RecordFailure();
        var after = DateTime.UtcNow;

        Assert.NotNull(cb.LastFailureUtc);
        Assert.True(cb.LastFailureUtc >= before);
        Assert.True(cb.LastFailureUtc <= after);
    }

    [Fact]
    public void DefaultThreshold_IsFive()
    {
        var cb = new CopilotCircuitBreaker();
        Assert.Equal(5, cb.FailureThreshold);
    }

    [Fact]
    public void DefaultOpenDuration_IsSixtySeconds()
    {
        var cb = new CopilotCircuitBreaker();
        Assert.Equal(TimeSpan.FromSeconds(60), cb.OpenDuration);
    }

    [Fact]
    public void CustomParameters_AreRespected()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 10, openDuration: TimeSpan.FromMinutes(5));
        Assert.Equal(10, cb.FailureThreshold);
        Assert.Equal(TimeSpan.FromMinutes(5), cb.OpenDuration);
    }

    [Fact]
    public void MoreFailuresBeyondThreshold_StaysOpen()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 2);
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.Equal(4, cb.ConsecutiveFailures);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentFailures_DoNotCorrupt()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 100);
        var tasks = new Task[50];

        for (int i = 0; i < 50; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 10; j++)
                {
                    cb.RecordFailure();
                    cb.AllowRequest();
                }
            });
        }

        await Task.WhenAll(tasks);

        // 50 threads × 10 failures = 500 total, should be >= threshold
        Assert.True(cb.ConsecutiveFailures >= 100);
        Assert.Equal(CircuitState.Open, cb.State);
    }

    [Fact]
    public async Task ThreadSafety_MixedSuccessAndFailure_DoNotCorrupt()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 100);
        var tasks = new Task[20];

        for (int i = 0; i < 20; i++)
        {
            var index = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    if (index % 2 == 0)
                        cb.RecordFailure();
                    else
                        cb.RecordSuccess();
                    cb.AllowRequest();
                }
            });
        }

        await Task.WhenAll(tasks);

        // No assertions on final state since it's nondeterministic,
        // but no exceptions should have been thrown (no corruption).
        var state = cb.State;
        Assert.True(state is CircuitState.Closed or CircuitState.Open or CircuitState.HalfOpen);
    }

    [Fact]
    public void Reset_DuringHalfOpen_RestoresClosed()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMilliseconds(50));
        cb.RecordFailure();
        Thread.Sleep(100);

        Assert.Equal(CircuitState.HalfOpen, cb.State);

        cb.Reset();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.AllowRequest());
    }

    [Fact]
    public void FailedProbe_RestartsCooldown()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMilliseconds(100));
        cb.RecordFailure(); // trip

        // Wait for half-open
        Thread.Sleep(150);
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // Probe request allowed
        Assert.True(cb.AllowRequest());

        // Probe fails — should restart cooldown
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        // Should NOT be half-open yet (cooldown just restarted)
        Thread.Sleep(50);
        Assert.Equal(CircuitState.Open, cb.State);

        // Wait full cooldown from the failed probe
        Thread.Sleep(100);
        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    [Fact]
    public void FullLifecycle_Closed_Open_HalfOpen_ProbeFail_Open_HalfOpen_ProbeSuccess_Closed()
    {
        var cb = new CopilotCircuitBreaker(failureThreshold: 2, openDuration: TimeSpan.FromMilliseconds(50));

        // Start closed
        Assert.Equal(CircuitState.Closed, cb.State);

        // Trip it
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.AllowRequest());

        // Wait for half-open
        Thread.Sleep(100);
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // Probe fails
        Assert.True(cb.AllowRequest());
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        // Wait again
        Thread.Sleep(100);
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // Probe succeeds
        Assert.True(cb.AllowRequest());
        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.AllowRequest());
    }
}
