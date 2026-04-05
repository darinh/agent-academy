namespace AgentAcademy.Server.Services;

/// <summary>
/// Circuit breaker for the Copilot API. Prevents burning through retries
/// when the API is consistently failing. Global (not per-agent) because
/// all agents share the same token and API endpoint.
///
/// States:
/// - Closed: requests flow normally.
/// - Open: consecutive failures exceeded threshold — immediately fallback.
/// - HalfOpen: cooldown elapsed — allow one probe request through.
///
/// Auth failures do NOT trip the circuit — they are handled by the
/// auth degradation pathway in CopilotExecutor.
/// </summary>
public sealed class CopilotCircuitBreaker
{
    private readonly object _lock = new();
    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTime _lastFailureUtc = DateTime.MinValue;
    private DateTime _lastStateChangeUtc = DateTime.UtcNow;

    /// <summary>Number of consecutive failures before opening the circuit.</summary>
    public int FailureThreshold { get; }

    /// <summary>How long the circuit stays open before allowing a probe.</summary>
    public TimeSpan OpenDuration { get; }

    public CopilotCircuitBreaker(int failureThreshold = 5, TimeSpan? openDuration = null)
    {
        FailureThreshold = failureThreshold;
        OpenDuration = openDuration ?? TimeSpan.FromSeconds(60);
    }

    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                return EvaluateState();
            }
        }
    }

    public int ConsecutiveFailures
    {
        get { lock (_lock) { return _consecutiveFailures; } }
    }

    public DateTime? LastFailureUtc
    {
        get
        {
            lock (_lock)
            {
                return _lastFailureUtc == DateTime.MinValue ? null : _lastFailureUtc;
            }
        }
    }

    /// <summary>
    /// Returns true if a request should be allowed through.
    /// In HalfOpen state, only the first caller gets through (transitions to probing).
    /// </summary>
    public bool AllowRequest()
    {
        lock (_lock)
        {
            var state = EvaluateState();
            switch (state)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open:
                    return false;

                case CircuitState.HalfOpen:
                    // Transition to Open during probe — only one request gets through.
                    // If it succeeds, RecordSuccess resets to Closed.
                    // If it fails, RecordFailure keeps it Open.
                    _state = CircuitState.Open;
                    _lastStateChangeUtc = DateTime.UtcNow;
                    return true;

                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// Records a successful API call. Resets the circuit to Closed.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_consecutiveFailures == 0 && _state == CircuitState.Closed)
                return;

            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
            _lastStateChangeUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Records a failed API call. If consecutive failures reach the
    /// threshold, opens the circuit.
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            _lastFailureUtc = DateTime.UtcNow;

            if (_consecutiveFailures >= FailureThreshold)
            {
                _state = CircuitState.Open;
                _lastStateChangeUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Manually resets the circuit to Closed. Called when external conditions
    /// change (e.g., new auth token provided).
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
            _lastStateChangeUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Evaluates whether the circuit should transition from Open to HalfOpen
    /// based on elapsed time. Must be called under lock.
    /// </summary>
    private CircuitState EvaluateState()
    {
        if (_state == CircuitState.Open)
        {
            var elapsed = DateTime.UtcNow - _lastStateChangeUtc;
            if (elapsed >= OpenDuration)
            {
                _state = CircuitState.HalfOpen;
                _lastStateChangeUtc = DateTime.UtcNow;
            }
        }

        return _state;
    }
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}
