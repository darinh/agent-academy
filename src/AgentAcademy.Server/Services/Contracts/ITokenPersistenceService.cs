namespace AgentAcademy.Server.Services.Contracts;

/// <summary>
/// Persists and restores OAuth tokens to/from the database (encrypted)
/// so the server can resume after a restart without requiring a browser login.
/// </summary>
public interface ITokenPersistenceService
{
    /// <summary>
    /// Removes all persisted token data from the database.
    /// Called on logout to ensure stale credentials don't survive a restart.
    /// </summary>
    Task ClearPersistedTokensAsync();
}
