using System.Collections.Concurrent;
using AgentAcademy.Server.Services.Contracts;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Thread-safe cache of <see cref="CopilotSession"/> instances with
/// sliding TTL expiration and per-key send serialization.
/// Sessions are created lazily via a caller-provided factory and
/// disposed automatically on expiry or invalidation.
/// </summary>
public sealed class CopilotSessionPool : ICopilotSessionPool
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(2);

    private readonly ILogger<CopilotSessionPool> _logger;
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();
    private Timer? _cleanupTimer;
    private bool _disposed;

    public CopilotSessionPool(ILogger<CopilotSessionPool> logger)
    {
        _logger = logger;
        _cleanupTimer = new Timer(
            _ => _ = CleanupExpiredAsync(),
            null,
            CleanupInterval,
            CleanupInterval);
    }

    /// <summary>
    /// Gets or creates a session for the given key, then executes
    /// <paramref name="action"/> while holding the per-session send lock.
    /// The session's TTL is refreshed on successful completion.
    /// </summary>
    public async Task<T> UseAsync<T>(
        string key,
        Func<CancellationToken, Task<CopilotSession>> sessionFactory,
        Func<CopilotSession, Task<T>> action,
        CancellationToken ct)
    {
        var entry = await GetOrCreateAsync(key, sessionFactory, ct);

        await entry.SendLock.WaitAsync(ct);
        try
        {
            var result = await action(entry.Session);
            entry.Touch();
            return result;
        }
        finally
        {
            entry.SendLock.Release();
        }
    }

    /// <summary>
    /// Removes and disposes the session for the given key.
    /// </summary>
    public async Task InvalidateAsync(string key)
    {
        if (_sessions.TryRemove(key, out var entry))
        {
            _keyLocks.TryRemove(key, out _);
            _logger.LogDebug("Invalidating session {Key}", key);
            await DisposeSessionSafeAsync(entry);
        }
    }

    /// <summary>
    /// Removes and disposes all sessions whose keys match <paramref name="predicate"/>.
    /// </summary>
    public async Task InvalidateByFilterAsync(Func<string, bool> predicate)
    {
        var keys = _sessions.Keys.Where(predicate).ToList();
        foreach (var key in keys)
        {
            if (_sessions.TryRemove(key, out var entry))
            {
                _keyLocks.TryRemove(key, out _);
                _logger.LogDebug("Invalidating session {Key}", key);
                await DisposeSessionSafeAsync(entry);
            }
        }
    }

    /// <summary>
    /// Removes and disposes all cached sessions.
    /// </summary>
    public async Task InvalidateAllAsync()
    {
        var keys = _sessions.Keys.ToList();
        _logger.LogInformation("Invalidating all {Count} agent sessions", keys.Count);

        foreach (var key in keys)
        {
            if (_sessions.TryRemove(key, out var entry))
            {
                _keyLocks.TryRemove(key, out _);
                await DisposeSessionSafeAsync(entry);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer?.Dispose();
        _cleanupTimer = null;

        foreach (var kvp in _sessions)
        {
            if (_sessions.TryRemove(kvp.Key, out var entry))
                await DisposeSessionSafeAsync(entry);
        }
    }

    // ── Internals ───────────────────────────────────────────────

    private async Task<SessionEntry> GetOrCreateAsync(
        string key,
        Func<CancellationToken, Task<CopilotSession>> sessionFactory,
        CancellationToken ct)
    {
        // Fast path — valid cached session.
        if (_sessions.TryGetValue(key, out var existing) && !existing.IsExpired)
        {
            existing.Touch();
            return existing;
        }

        // Slow path — per-key lock for creation.
        var keyLock = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring lock.
            if (_sessions.TryGetValue(key, out existing) && !existing.IsExpired)
            {
                existing.Touch();
                return existing;
            }

            // Dispose expired entry if present.
            if (existing is not null)
            {
                _sessions.TryRemove(key, out _);
                await DisposeSessionSafeAsync(existing);
            }

            _logger.LogDebug("Creating new session for key {Key}", key);

            var session = await sessionFactory(ct);
            var entry = new SessionEntry(session);
            _sessions[key] = entry;
            return entry;
        }
        finally
        {
            keyLock.Release();
        }
    }

    private async Task CleanupExpiredAsync()
    {
        var expired = _sessions
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
        {
            if (_sessions.TryRemove(key, out var entry))
            {
                _keyLocks.TryRemove(key, out _);
                _logger.LogDebug("Cleaning up expired session {Key}", key);
                await DisposeSessionSafeAsync(entry);
            }
        }

        if (expired.Count > 0)
            _logger.LogInformation("Cleaned up {Count} expired session(s)", expired.Count);
    }

    private async Task DisposeSessionSafeAsync(SessionEntry entry)
    {
        try
        {
            await entry.Session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing CopilotSession");
        }
    }

    // ── Session entry with TTL tracking ─────────────────────────

    private sealed class SessionEntry
    {
        public CopilotSession Session { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        private DateTime _lastUsed;

        public SessionEntry(CopilotSession session)
        {
            Session = session;
            _lastUsed = DateTime.UtcNow;
        }

        public bool IsExpired => DateTime.UtcNow - _lastUsed > SessionTtl;

        public void Touch() => _lastUsed = DateTime.UtcNow;
    }
}
