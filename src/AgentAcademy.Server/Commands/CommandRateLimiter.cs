using System.Collections.Concurrent;

namespace AgentAcademy.Server.Commands;

/// <summary>
/// Per-agent sliding-window rate limiter for command execution.
/// Prevents agents from spamming commands in tight loops.
/// Thread-safe; designed as a singleton.
/// </summary>
public sealed class CommandRateLimiter
{
    private int _maxCommands;
    private TimeSpan _window;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _timestamps = new();

    public CommandRateLimiter(int maxCommands = 30, int windowSeconds = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCommands, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(windowSeconds, 0);
        _maxCommands = maxCommands;
        _window = TimeSpan.FromSeconds(windowSeconds);
    }

    /// <summary>
    /// Reconfigures the rate limiter at runtime. Existing windows continue
    /// with the new limits applied on the next TryAcquire call.
    /// </summary>
    public void Configure(int maxCommands, int windowSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCommands, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(windowSeconds, 0);
        _maxCommands = maxCommands;
        _window = TimeSpan.FromSeconds(windowSeconds);
    }

    public int MaxCommands => _maxCommands;
    public int WindowSeconds => (int)_window.TotalSeconds;

    /// <summary>
    /// Attempt to acquire a rate-limit token for the given agent.
    /// Returns true if allowed, false if rate-limited.
    /// When rate-limited, <paramref name="retryAfterSeconds"/> indicates how long to wait.
    /// </summary>
    public bool TryAcquire(string agentId, out int retryAfterSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var now = DateTime.UtcNow;
        var queue = _timestamps.GetOrAdd(agentId, _ => new Queue<DateTime>());

        lock (queue)
        {
            // Evict expired entries
            while (queue.Count > 0 && now - queue.Peek() >= _window)
                queue.Dequeue();

            if (queue.Count >= _maxCommands)
            {
                var oldest = queue.Peek();
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((_window - (now - oldest)).TotalSeconds));
                return false;
            }

            queue.Enqueue(now);
            retryAfterSeconds = 0;
            return true;
        }
    }

    /// <summary>
    /// Returns the number of commands used in the current window for the given agent.
    /// </summary>
    public int GetCurrentCount(string agentId)
    {
        if (!_timestamps.TryGetValue(agentId, out var queue))
            return 0;

        var now = DateTime.UtcNow;
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= _window)
                queue.Dequeue();
            return queue.Count;
        }
    }
}
