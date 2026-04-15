namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Manages the lifecycle of CopilotClient instances —
/// one default client and zero-or-more worktree-scoped clients.
/// Owns token resolution, client creation/disposal, and token-rotation
/// detection for both client pools.
/// </summary>
public interface ICopilotClientFactory : IAsyncDisposable
{
    /// <summary>
    /// True once the default CopilotClient has been
    /// successfully started and hasn't failed.
    /// </summary>
    bool IsDefaultClientOperational { get; }

    /// <summary>
    /// Acquires the default (non-worktree) client. Creates it on first
    /// call; recreates if the auth token has changed since last creation.
    /// </summary>
    Task<ClientAcquisitionResult> GetClientAsync(CancellationToken ct);

    /// <summary>
    /// Acquires a client scoped to a git worktree directory.
    /// Detects token rotation and invalidates all worktree clients
    /// to prevent stale-token usage.
    /// </summary>
    Task<ClientAcquisitionResult> GetWorktreeClientAsync(string workspacePath, CancellationToken ct);

    /// <summary>
    /// Disposes a worktree-scoped client. Returns the session key
    /// prefix so callers can invalidate matching sessions.
    /// </summary>
    Task<string?> DisposeWorktreeClientAsync(string workspacePath);
}
