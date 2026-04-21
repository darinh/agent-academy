using GitHub.Copilot.SDK;

namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Thread-safe cache of CopilotSession instances with sliding TTL
/// expiration and per-key send serialization. Sessions are created
/// lazily via a caller-provided factory and disposed automatically
/// on expiry or invalidation.
/// </summary>
public interface ICopilotSessionPool : IAsyncDisposable
{
    /// <summary>
    /// Gets or creates a session for the given key, then executes
    /// <paramref name="action"/> while holding the per-session send lock.
    /// The session's TTL is refreshed on successful completion.
    /// </summary>
    Task<T> UseAsync<T>(
        string key,
        Func<CancellationToken, Task<CopilotSession>> sessionFactory,
        Func<CopilotSession, Task<T>> action,
        CancellationToken ct);

    /// <summary>
    /// Removes and disposes the session for the given key.
    /// </summary>
    Task InvalidateAsync(string key);

    /// <summary>
    /// Removes and disposes all sessions whose keys match <paramref name="predicate"/>.
    /// </summary>
    Task InvalidateByFilterAsync(Func<string, bool> predicate);

    /// <summary>
    /// Removes and disposes all cached sessions.
    /// </summary>
    Task InvalidateAllAsync();
}
