using AgentAcademy.Server.Services;
using Microsoft.Extensions.Time.Testing;

namespace AgentAcademy.Server.Tests;

public class CopilotCircuitBreakerTests
{
    private readonly FakeTimeProvider _time = new();

    private CopilotCircuitBreaker Create(int failureThreshold = 5, TimeSpan? openDuration = null)
        => new(failureThreshold, openDuration, _time);

    [Fact]
    public void InitialState_IsClosed()
    {
        var cb = Create();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.ConsecutiveFailures);
        Assert.Null(cb.LastFailureUtc);
    }

    [Fact]
    public void AllowRequest_WhenClosed_ReturnsTrue()
    {
        var cb = Create();
        Assert.True(cb.AllowRequest());
    }

    [Fact]
    public void RecordSuccess_WhenClosed_RemainsClosedAndZeroFailures()
    {
        var cb = Create();
        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.ConsecutiveFailures);
    }

    [Fact]
    public void RecordFailure_BelowThreshold_RemainsClosed()
    {
        var cb = Create(failureThreshold: 5);

        for (int i = 0; i < 4; i++)
            cb.RecordFailure();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(4, cb.ConsecutiveFailures);
        Assert.True(cb.AllowRequest());
    }

    [Fact]
    public void RecordFailure_AtThreshold_OpensCircuit()
    {
        var cb = Create(failureThreshold: 3);

        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordFailure();

        Assert.Equal(CircuitState.Open, cb.State);
        Assert.Equal(3, cb.ConsecutiveFailures);
    }

    [Fact]
    public void AllowRequest_WhenOpen_ReturnsFalse()
    {
        var cb = Create(failureThreshold: 1);
        cb.RecordFailure();

        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.AllowRequest());
    }

    [Fact]
    public void Open_TransitionsToHalfOpen_AfterCooldown()
    {
        var cb = Create(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(30));
        cb.RecordFailure();

        Assert.Equal(CircuitState.Open, cb.State);

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    [Fact]
    public void HalfOpen_AllowsOneRequest()
    {
        var cb = Create(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(30));
        cb.RecordFailure();

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // First request allowed (probe)
        Assert.True(cb.AllowRequest());

        // Second request blocked (circuit returned to Open during probe)
        Assert.False(cb.AllowRequest());
    }

    [Fact]
    public void HalfOpen_ProbeSuccess_ResetsToClosed()
    {
        var cb = Create(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(30));
        cb.RecordFailure();

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(cb.AllowRequest()); // probe allowed

        cb.RecordSuccess();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.ConsecutiveFailures);
        Assert.True(cb.AllowRequest());
    }

    [Fact]
    public void HalfOpen_ProbeFailure_ReopensCircuit()
    {
        var cb = Create(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(30));
        cb.RecordFailure();

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(cb.AllowRequest()); // probe allowed

        cb.RecordFailure();

        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.AllowRequest());
    }

    [Fact]
    public void RecordSuccess_ResetsConsecutiveFailures()
    {
        var cb = Create(failureThreshold: 5);

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
        var cb = Create(failureThreshold: 3);

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
        var cb = Create(failureThreshold: 1);
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
        var cb = Create();
        Assert.Null(cb.LastFailureUtc);

        var before = _time.GetUtcNow().UtcDateTime;
        cb.RecordFailure();
        var after = _time.GetUtcNow().UtcDateTime;

        Assert.NotNull(cb.LastFailureUtc);
        Assert.True(cb.LastFailureUtc >= before);
        Assert.True(cb.LastFailureUtc <= after);
    }

    [Fact]
    public void DefaultThreshold_IsFive()
    {
        var cb = Create();
        Assert.Equal(5, cb.FailureThreshold);
    }

    [Fact]
    public void DefaultOpenDuration_IsSixtySeconds()
    {
        var cb = Create();
        Assert.Equal(TimeSpan.FromSeconds(60), cb.OpenDuration);
    }

    [Fact]
    public void CustomParameters_AreRespected()
    {
        var cb = Create(failureThreshold: 10, openDuration: TimeSpan.FromMinutes(5));
        Assert.Equal(10, cb.FailureThreshold);
        Assert.Equal(TimeSpan.FromMinutes(5), cb.OpenDuration);
    }

    [Fact]
    public void MoreFailuresBeyondThreshold_StaysOpen()
    {
        var cb = Create(failureThreshold: 2);
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
        var cb = Create(failureThreshold: 100);
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
        var cb = Create(failureThreshold: 100);
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
        var cb = Create(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(30));
        cb.RecordFailure();
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(CircuitState.HalfOpen, cb.State);

        cb.Reset();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.AllowRequest());
    }

    [Fact]
    public void FailedProbe_RestartsCooldown()
    {
        var cb = Create(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(60));
        cb.RecordFailure(); // trip

        // Advance past cooldown → half-open
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // Probe request allowed
        Assert.True(cb.AllowRequest());

        // Probe fails — should restart cooldown
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        // Should NOT be half-open yet (cooldown just restarted)
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(CircuitState.Open, cb.State);

        // Wait full cooldown from the failed probe
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    [Fact]
    public void FullLifecycle_Closed_Open_HalfOpen_ProbeFail_Open_HalfOpen_ProbeSuccess_Closed()
    {
        var cb = Create(failureThreshold: 2, openDuration: TimeSpan.FromSeconds(30));

        // Start closed
        Assert.Equal(CircuitState.Closed, cb.State);

        // Trip it
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.AllowRequest());

        // Wait for half-open
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // Probe fails
        Assert.True(cb.AllowRequest());
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        // Wait again
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // Probe succeeds
        Assert.True(cb.AllowRequest());
        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.AllowRequest());
    }

    [Fact]
    public void Open_DoesNotTransition_BeforeCooldownExpires()
    {
        var cb = Create(failureThreshold: 1, openDuration: TimeSpan.FromSeconds(60));
        cb.RecordFailure();

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(CircuitState.Open, cb.State);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }
}
